// SPDX-License-Identifier: GPL-3.0-only
#define _GNU_SOURCE
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <inttypes.h>
#include <linux/openat2.h>
#include <signal.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/file.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <time.h>
#include <unistd.h>

#ifndef O_PATH
#define O_PATH 010000000
#endif
#ifndef AT_EMPTY_PATH
#define AT_EMPTY_PATH 0x1000
#endif

#define EXIT_ARGUMENTS 20
#define EXIT_CONTEXT 21
#define EXIT_SOURCE 22
#define EXIT_DESTINATION 23
#define EXIT_LINK 24
#define EXIT_PLATFORM 25
#define EXIT_AUTHORIZATION 26
#define RECORD_LIMIT (64U * 1024U)
#define PATH_LIMIT 32768U
#define APPROVED_FIXED 148U
#define CLAIM_FIXED 64U
#define RESULT_SIZE 64U

static void fail(int code, const char *message)
{
    dprintf(STDERR_FILENO, "hardlink import: %s\n", message);
    _exit(code);
}

static bool safe_absolute_path(const char *path)
{
    if (path == NULL || path[0] != '/' || path[1] == '\0') return false;
    const char *component = path + 1;
    for (const unsigned char *cursor = (const unsigned char *)path; *cursor; cursor++)
        if (*cursor < 0x20 || *cursor == 0x7f) return false;
    while (*component)
    {
        const char *slash = strchr(component, '/');
        size_t length = slash == NULL ? strlen(component) : (size_t)(slash - component);
        if (length == 0 || (length == 1 && component[0] == '.') ||
            (length == 2 && component[0] == '.' && component[1] == '.')) return false;
        if (slash == NULL) break;
        component = slash + 1;
    }
    return true;
}

static bool path_is_within_root(const char *path, const char *root)
{
    size_t root_length = strlen(root);
    return root_length > 1 && root[root_length - 1] != '/' &&
        strncmp(path, root, root_length) == 0 && path[root_length] == '/' &&
        path[root_length + 1] != '\0';
}

static bool valid_utf8(const unsigned char *value, size_t length)
{
    size_t index = 0;
    while (index < length)
    {
        unsigned char first = value[index++];
        if (first < 0x80) continue;
        unsigned needed;
        uint32_t code;
        if (first >= 0xc2 && first <= 0xdf) { needed = 1; code = first & 0x1f; }
        else if (first >= 0xe0 && first <= 0xef) { needed = 2; code = first & 0x0f; }
        else if (first >= 0xf0 && first <= 0xf4) { needed = 3; code = first & 0x07; }
        else return false;
        if (index + needed > length) return false;
        for (unsigned offset = 0; offset < needed; offset++)
        {
            unsigned char next = value[index++];
            if ((next & 0xc0) != 0x80) return false;
            code = (code << 6) | (next & 0x3f);
        }
        if ((needed == 2 && code < 0x800) || (needed == 3 && code < 0x10000) ||
            code > 0x10ffff || (code >= 0xd800 && code <= 0xdfff)) return false;
    }
    return true;
}

static int secure_openat2(int directory, const char *path, unsigned long flags)
{
    struct open_how how = {
        .flags = flags,
        .resolve = RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS | RESOLVE_NO_MAGICLINKS
    };
    return (int)syscall(SYS_openat2, directory, path, &how, sizeof(how));
}

static uint32_t read_u32(const unsigned char *p)
{
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}

static uint64_t read_u64(const unsigned char *p)
{
    uint64_t value = 0;
    for (unsigned index = 0; index < 8; index++) value |= (uint64_t)p[index] << (index * 8);
    return value;
}

static void write_u32(unsigned char *p, uint32_t value)
{
    for (unsigned index = 0; index < 4; index++) p[index] = (unsigned char)(value >> (index * 8));
}

static void write_u64(unsigned char *p, uint64_t value)
{
    for (unsigned index = 0; index < 8; index++) p[index] = (unsigned char)(value >> (index * 8));
}

static bool parse_positive_id(const char *value, uint32_t *result)
{
    if (value == NULL || *value == '\0') return false;
    errno = 0;
    char *end = NULL;
    unsigned long parsed = strtoul(value, &end, 10);
    if (errno != 0 || end == value || *end != '\0' || parsed == 0 || parsed > UINT32_MAX) return false;
    *result = (uint32_t)parsed;
    return true;
}

static bool same_regular_inode(int descriptor, const struct stat *source)
{
    struct stat candidate;
    return fstat(descriptor, &candidate) == 0 && S_ISREG(candidate.st_mode) &&
        candidate.st_dev == source->st_dev && candidate.st_ino == source->st_ino;
}

static bool path_still_names_inode(int root, const char *absolute_path, const struct stat *expected)
{
    int current = secure_openat2(root, absolute_path + 1, O_PATH | O_NOFOLLOW | O_CLOEXEC);
    if (current < 0) return false;
    bool same = same_regular_inode(current, expected);
    close(current);
    return same;
}

static bool directory_path_still_names_inode(int root, const char *relative_path, const struct stat *expected)
{
    int current = secure_openat2(root, relative_path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
    if (current < 0) return false;
    struct stat status;
    bool same = fstat(current, &status) == 0 && status.st_dev == expected->st_dev && status.st_ino == expected->st_ino;
    close(current);
    return same;
}

static bool read_record(int descriptor, unsigned char *buffer, size_t *length)
{
    struct stat status;
    if (fstat(descriptor, &status) != 0 || !S_ISREG(status.st_mode) || status.st_uid != geteuid() ||
        (status.st_mode & 07777) != 0600 || status.st_nlink != 1 || status.st_size <= 0 ||
        status.st_size > (off_t)RECORD_LIMIT) return false;
    size_t wanted = (size_t)status.st_size;
    size_t offset = 0;
    while (offset < wanted)
    {
        ssize_t count = pread(descriptor, buffer + offset, wanted - offset, (off_t)offset);
        if (count <= 0) return false;
        offset += (size_t)count;
    }
    *length = wanted;
    return true;
}

static bool valid_hex_id(const char *name, unsigned char id[32])
{
    if (strlen(name) != 73 || strcmp(name + 64, ".approved") != 0) return false;
    for (unsigned index = 0; index < 32; index++)
    {
        char pair[3] = { name[index * 2], name[index * 2 + 1], 0 };
        if (!((pair[0] >= '0' && pair[0] <= '9') || (pair[0] >= 'a' && pair[0] <= 'f')) ||
            !((pair[1] >= '0' && pair[1] <= '9') || (pair[1] >= 'a' && pair[1] <= 'f'))) return false;
        id[index] = (unsigned char)strtoul(pair, NULL, 16);
    }
    return true;
}

typedef struct {
    unsigned char id[32];
    char hex[65];
    uint64_t expires;
    uint64_t device;
    uint64_t inode;
    int64_t size;
    int64_t mtime_sec;
    int64_t mtime_nsec;
    int64_t ctime_sec;
    int64_t ctime_nsec;
    uint32_t mode;
    uint32_t uid;
    uint32_t gid;
    uint32_t artist;
    uint32_t album;
    uint32_t track;
    char source_path[PATH_LIMIT + 1];
    char destination_root[PATH_LIMIT + 1];
} approved_record;

static bool parse_approved_bytes(const unsigned char *bytes, size_t length, const char *name, approved_record *record)
{
    unsigned char filename_id[32];
    if (!valid_hex_id(name, filename_id) || length < APPROVED_FIXED ||
        memcmp(bytes, "AIAUTH02", 8) != 0 || read_u32(bytes + 8) != 2 ||
        memcmp(bytes + 12, filename_id, 32) != 0) return false;
    uint32_t path_length = read_u32(bytes + 140);
    uint32_t root_length = read_u32(bytes + 144);
    if (path_length == 0 || path_length > PATH_LIMIT || root_length == 0 || root_length > PATH_LIMIT ||
        length != APPROVED_FIXED + path_length + root_length ||
        memchr(bytes + APPROVED_FIXED, '\0', path_length) != NULL ||
        memchr(bytes + APPROVED_FIXED + path_length, '\0', root_length) != NULL ||
        !valid_utf8(bytes + APPROVED_FIXED, path_length) ||
        !valid_utf8(bytes + APPROVED_FIXED + path_length, root_length)) return false;
    memcpy(record->id, bytes + 12, 32);
    memcpy(record->source_path, bytes + APPROVED_FIXED, path_length);
    record->source_path[path_length] = '\0';
    memcpy(record->destination_root, bytes + APPROVED_FIXED + path_length, root_length);
    record->destination_root[root_length] = '\0';
    if (!safe_absolute_path(record->source_path) || !safe_absolute_path(record->destination_root) ||
        record->destination_root[strlen(record->destination_root) - 1] == '/') return false;
    memcpy(record->hex, name, 64);
    record->hex[64] = '\0';
    uint64_t created = read_u64(bytes + 44);
    record->expires = read_u64(bytes + 52);
    record->device = read_u64(bytes + 60);
    record->inode = read_u64(bytes + 68);
    record->size = (int64_t)read_u64(bytes + 76);
    record->mtime_sec = (int64_t)read_u64(bytes + 84);
    record->mtime_nsec = (int64_t)read_u64(bytes + 92);
    record->ctime_sec = (int64_t)read_u64(bytes + 100);
    record->ctime_nsec = (int64_t)read_u64(bytes + 108);
    record->mode = read_u32(bytes + 116);
    record->uid = read_u32(bytes + 120);
    record->gid = read_u32(bytes + 124);
    record->artist = read_u32(bytes + 128);
    record->album = read_u32(bytes + 132);
    record->track = read_u32(bytes + 136);
    return record->expires > created && record->expires <= created + 86400 &&
        record->artist > 0 && record->album > 0 && record->track > 0 && record->size >= 0 &&
        record->mtime_nsec >= 0 && record->mtime_nsec < 1000000000 &&
        record->ctime_nsec >= 0 && record->ctime_nsec < 1000000000;
}

static bool parse_approved(int descriptor, const char *name, approved_record *record)
{
    unsigned char bytes[RECORD_LIMIT];
    size_t length = 0;
    return read_record(descriptor, bytes, &length) && parse_approved_bytes(bytes, length, name, record);
}

static bool identity_matches(const approved_record *record, const struct stat *status, bool include_ctime)
{
    return record->device == (uint64_t)status->st_dev && record->inode == (uint64_t)status->st_ino &&
        record->size == (int64_t)status->st_size && record->mtime_sec == (int64_t)status->st_mtim.tv_sec &&
        record->mtime_nsec == status->st_mtim.tv_nsec && record->mode == (uint32_t)(status->st_mode & 07777) &&
        record->uid == status->st_uid && record->gid == status->st_gid &&
        (!include_ctime || (record->ctime_sec == (int64_t)status->st_ctim.tv_sec && record->ctime_nsec == status->st_ctim.tv_nsec));
}

static void make_name(char *output, size_t size, const char *hex, const char *suffix)
{
    if (snprintf(output, size, "%s%s", hex, suffix) >= (int)size) fail(EXIT_PLATFORM, "authorization filename overflow");
}

static int parse_claim(int authfd, const approved_record *approved, const char *destination,
    const struct stat *parent, int *claimfd)
{
    char name[80];
    make_name(name, sizeof(name), approved->hex, ".claim");
    int descriptor = openat(authfd, name, O_RDWR | O_NOFOLLOW | O_CLOEXEC);
    if (descriptor < 0) return errno == ENOENT ? 0 : -1;
    unsigned char bytes[RECORD_LIMIT];
    size_t length = 0;
    bool valid = read_record(descriptor, bytes, &length) && length >= CLAIM_FIXED &&
        memcmp(bytes, "AICLAIM", 8) == 0 && read_u32(bytes + 8) == 1 &&
        memcmp(bytes + 12, approved->id, 32) == 0 &&
        read_u64(bytes + 44) == (uint64_t)parent->st_dev && read_u64(bytes + 52) == (uint64_t)parent->st_ino;
    uint32_t path_length = valid ? read_u32(bytes + 60) : 0;
    valid = valid && path_length > 0 && path_length <= PATH_LIMIT && length == CLAIM_FIXED + path_length &&
        strlen(destination) == path_length && memcmp(bytes + CLAIM_FIXED, destination, path_length) == 0;
    if (!valid) { close(descriptor); return -1; }
    *claimfd = descriptor;
    return 1;
}

static int publish_immutable(int authfd, const char *name, const unsigned char *bytes, size_t length)
{
    int temporary = openat(authfd, ".", O_TMPFILE | O_RDWR | O_CLOEXEC, 0600);
    if (temporary < 0) fail(EXIT_AUTHORIZATION, "authorization directory does not support anonymous durable records");
    size_t offset = 0;
    while (offset < length)
    {
        ssize_t count = write(temporary, bytes + offset, length - offset);
        if (count <= 0) fail(EXIT_AUTHORIZATION, "cannot write durable authorization record");
        offset += (size_t)count;
    }
    if (fdatasync(temporary) != 0) fail(EXIT_AUTHORIZATION, "cannot synchronize authorization record");
    if (linkat(temporary, "", authfd, name, AT_EMPTY_PATH) != 0)
    {
        char proc_record[64];
        if (snprintf(proc_record, sizeof(proc_record), "/proc/self/fd/%d", temporary) >= (int)sizeof(proc_record) ||
            linkat(AT_FDCWD, proc_record, authfd, name, AT_SYMLINK_FOLLOW) != 0)
        {
            int saved = errno;
            close(temporary);
            errno = saved;
            return -1;
        }
    }
    if (fsync(authfd) != 0) fail(EXIT_AUTHORIZATION, "cannot synchronize authorization directory");
    close(temporary);
    return 0;
}

static int open_authorization_directory(const char *path)
{
    if (!safe_absolute_path(path) || !valid_utf8((const unsigned char *)path, strlen(path)))
        fail(EXIT_AUTHORIZATION, "authorization directory path is invalid");
    int root = open("/", O_PATH | O_DIRECTORY | O_CLOEXEC);
    if (root < 0) fail(EXIT_PLATFORM, "cannot open filesystem root");
    int directory = secure_openat2(root, path + 1, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
    close(root);
    struct stat status;
    if (directory < 0 || fstat(directory, &status) != 0 || status.st_uid != geteuid() ||
        (status.st_mode & 07777) != 0700)
        fail(EXIT_AUTHORIZATION, "authorization directory must be owned by this user with mode 0700");
    return directory;
}

static void publish_approved_from_stdin(const char *authorization_path, const char *name)
{
    unsigned char bytes[RECORD_LIMIT];
    size_t length = 0;
    while (length < sizeof(bytes))
    {
        ssize_t count = read(STDIN_FILENO, bytes + length, sizeof(bytes) - length);
        if (count < 0) fail(EXIT_AUTHORIZATION, "cannot read transfer authorization record");
        if (count == 0) break;
        length += (size_t)count;
    }
    unsigned char overflow;
    if (length == sizeof(bytes) && read(STDIN_FILENO, &overflow, 1) != 0)
        fail(EXIT_AUTHORIZATION, "transfer authorization record is too large");
    approved_record proposed;
    if (!parse_approved_bytes(bytes, length, name, &proposed))
        fail(EXIT_AUTHORIZATION, "transfer authorization record is malformed");

    int authfd = open_authorization_directory(authorization_path);
    int lockfd = openat(authfd, ".publish.lock", O_RDWR | O_CREAT | O_NOFOLLOW | O_CLOEXEC, 0600);
    struct stat lock_status;
    if (lockfd < 0 || fstat(lockfd, &lock_status) != 0 || !S_ISREG(lock_status.st_mode) ||
        lock_status.st_uid != geteuid() || (lock_status.st_mode & 07777) != 0600 || lock_status.st_nlink != 1)
        fail(EXIT_AUTHORIZATION, "authorization publication lock is unsafe");
    if (flock(lockfd, LOCK_EX) != 0) fail(EXIT_AUTHORIZATION, "cannot lock authorization publication");

    DIR *directory = fdopendir(dup(authfd));
    if (directory == NULL) fail(EXIT_AUTHORIZATION, "cannot enumerate authorization directory");
    struct dirent *entry;
    while ((entry = readdir(directory)) != NULL)
    {
        if (strlen(entry->d_name) != 73 || strcmp(entry->d_name + 64, ".approved") != 0) continue;
        int descriptor = openat(authfd, entry->d_name, O_RDONLY | O_NOFOLLOW | O_CLOEXEC);
        if (descriptor < 0) fail(EXIT_AUTHORIZATION, "cannot inspect existing authorization");
        approved_record existing;
        bool valid = parse_approved(descriptor, entry->d_name, &existing);
        close(descriptor);
        if (!valid) fail(EXIT_AUTHORIZATION, "existing authorization record is malformed");
        if (existing.device == proposed.device && existing.inode == proposed.inode &&
            existing.expires >= (uint64_t)time(NULL))
            fail(EXIT_AUTHORIZATION, "an active authorization already exists for this source inode");
    }
    closedir(directory);
    if (publish_immutable(authfd, name, bytes, length) != 0)
        fail(EXIT_AUTHORIZATION, errno == EEXIST ? "authorization ID already exists" : "cannot publish transfer authorization");
    dprintf(STDOUT_FILENO, "hardlink import: transfer authorization published\n");
    _exit(0);
}

static int create_or_open_claim(int authfd, const approved_record *approved, const char *destination,
    const struct stat *parent)
{
    int existing = -1;
    if (parse_claim(authfd, approved, destination, parent, &existing) == 1) return existing;
    size_t path_length = strlen(destination);
    if (path_length == 0 || path_length > PATH_LIMIT) fail(EXIT_AUTHORIZATION, "destination path is too long for authorization");
    unsigned char bytes[CLAIM_FIXED + PATH_LIMIT];
    memcpy(bytes, "AICLAIM", 8);
    write_u32(bytes + 8, 1);
    memcpy(bytes + 12, approved->id, 32);
    write_u64(bytes + 44, (uint64_t)parent->st_dev);
    write_u64(bytes + 52, (uint64_t)parent->st_ino);
    write_u32(bytes + 60, (uint32_t)path_length);
    memcpy(bytes + CLAIM_FIXED, destination, path_length);
    char name[80];
    make_name(name, sizeof(name), approved->hex, ".claim");
    if (publish_immutable(authfd, name, bytes, CLAIM_FIXED + path_length) != 0 && errno != EEXIST)
        fail(EXIT_AUTHORIZATION, "cannot publish transfer claim");
    if (parse_claim(authfd, approved, destination, parent, &existing) != 1)
        fail(EXIT_AUTHORIZATION, "cannot reopen transfer claim");
    return existing;
}

static uint32_t result_outcome(int authfd, const approved_record *approved, const struct stat *source)
{
    char name[80];
    make_name(name, sizeof(name), approved->hex, ".result");
    int descriptor = openat(authfd, name, O_RDONLY | O_NOFOLLOW | O_CLOEXEC);
    if (descriptor < 0)
    {
        if (errno == ENOENT) return 0;
        fail(EXIT_AUTHORIZATION, "cannot open durable transfer result");
    }
    unsigned char bytes[RESULT_SIZE];
    size_t length = 0;
    uint32_t outcome = 0;
    bool valid = read_record(descriptor, bytes, &length) && length == RESULT_SIZE &&
        memcmp(bytes, "AIRESULT", 8) == 0 && read_u32(bytes + 8) == 1 &&
        memcmp(bytes + 12, approved->id, 32) == 0 &&
        ((outcome = read_u32(bytes + 44)) == 1 || outcome == 2 || outcome == 3) &&
        read_u64(bytes + 48) == (uint64_t)source->st_dev && read_u64(bytes + 56) == (uint64_t)source->st_ino;
    close(descriptor);
    if (!valid) fail(EXIT_AUTHORIZATION, "durable transfer result is invalid");
    return outcome;
}

static void publish_result(int authfd, const approved_record *approved, const struct stat *source, uint32_t outcome)
{
    unsigned char bytes[RESULT_SIZE];
    memcpy(bytes, "AIRESULT", 8);
    write_u32(bytes + 8, 1);
    memcpy(bytes + 12, approved->id, 32);
    write_u32(bytes + 44, outcome);
    write_u64(bytes + 48, (uint64_t)source->st_dev);
    write_u64(bytes + 56, (uint64_t)source->st_ino);
    char name[80];
    make_name(name, sizeof(name), approved->hex, ".result");
    if (publish_immutable(authfd, name, bytes, sizeof(bytes)) != 0 && errno != EEXIST)
        fail(EXIT_AUTHORIZATION, "cannot publish durable transfer result");
    if (result_outcome(authfd, approved, source) != outcome)
        fail(EXIT_AUTHORIZATION, "cannot verify durable transfer result");
}

#ifdef AIIMPORT_TEST_HOOKS
static void test_pause(const char *environment_name)
{
    const char *base = getenv(environment_name);
    if (base == NULL || *base == '\0') return;
    char ready[4096]; char proceed[4096];
    if (snprintf(ready, sizeof(ready), "%s.ready", base) >= (int)sizeof(ready) ||
        snprintf(proceed, sizeof(proceed), "%s.go", base) >= (int)sizeof(proceed)) _exit(90);
    int marker = open(ready, O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC, 0600);
    if (marker < 0) _exit(91);
    close(marker);
    while (access(proceed, F_OK) != 0) usleep(1000);
}
#else
#define test_pause(name) ((void)0)
#endif

static void verify_result_command(const char *authorization_path, const char *authorization_id,
    const char *destination_path)
{
    if (strlen(authorization_id) != 64 || !safe_absolute_path(destination_path) ||
        !valid_utf8((const unsigned char *)destination_path, strlen(destination_path)))
        fail(EXIT_ARGUMENTS, "result verification arguments are invalid");
    for (size_t index = 0; index < 64; index++)
        if (!((authorization_id[index] >= '0' && authorization_id[index] <= '9') ||
              (authorization_id[index] >= 'a' && authorization_id[index] <= 'f')))
            fail(EXIT_ARGUMENTS, "authorization ID must be 64 lowercase hexadecimal characters");

    int authfd = open_authorization_directory(authorization_path);
    char approved_name[80];
    if (snprintf(approved_name, sizeof(approved_name), "%s.approved", authorization_id) >= (int)sizeof(approved_name))
        fail(EXIT_ARGUMENTS, "authorization ID is too long");
    int approvedfd = openat(authfd, approved_name, O_RDONLY | O_NOFOLLOW | O_CLOEXEC);
    approved_record approved;
    if (approvedfd < 0 || !parse_approved(approvedfd, approved_name, &approved))
        fail(EXIT_AUTHORIZATION, "approved transfer record is missing or invalid");
    close(approvedfd);
    if (!path_is_within_root(destination_path, approved.destination_root))
        fail(EXIT_AUTHORIZATION, "destination is outside the approved root");

    char *destination_copy = strdup(destination_path + 1);
    if (destination_copy == NULL) fail(EXIT_PLATFORM, "memory allocation failed");
    char *separator = strrchr(destination_copy, '/');
    const char *destination_name; const char *destination_parent;
    if (separator == NULL) { destination_parent = "."; destination_name = destination_copy; }
    else { *separator = '\0'; destination_parent = destination_copy; destination_name = separator + 1; }
    if (*destination_name == '\0' || strcmp(destination_name, ".") == 0 || strcmp(destination_name, "..") == 0)
        fail(EXIT_ARGUMENTS, "destination basename is invalid");

    int root = open("/", O_PATH | O_DIRECTORY | O_CLOEXEC);
    if (root < 0) fail(EXIT_PLATFORM, "cannot open filesystem root");
    int source = secure_openat2(root, approved.source_path + 1, O_PATH | O_NOFOLLOW | O_CLOEXEC);
    struct stat source_status;
    if (source < 0 || fstat(source, &source_status) != 0 || !S_ISREG(source_status.st_mode) ||
        !identity_matches(&approved, &source_status, false))
        fail(EXIT_SOURCE, "approved source identity is missing or changed");
    int destination_directory = secure_openat2(root, destination_parent,
        O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
    struct stat parent_status;
    if (destination_directory < 0 || fstat(destination_directory, &parent_status) != 0)
        fail(EXIT_DESTINATION, "destination parent cannot be verified");
    int claimfd = -1;
    if (parse_claim(authfd, &approved, destination_path, &parent_status, &claimfd) != 1)
        fail(EXIT_AUTHORIZATION, "durable transfer claim does not match the expected destination");
    if (flock(claimfd, LOCK_EX) != 0) fail(EXIT_AUTHORIZATION, "cannot lock transfer claim for verification");
    if (fstat(source, &source_status) != 0 || !identity_matches(&approved, &source_status, false))
        fail(EXIT_SOURCE, "approved source identity changed during verification");
    uint32_t outcome = result_outcome(authfd, &approved, &source_status);
    if (outcome != 1 && outcome != 2)
        fail(EXIT_AUTHORIZATION, "durable transfer result is absent or unsuccessful");
    int destination = openat(destination_directory, destination_name, O_PATH | O_NOFOLLOW | O_CLOEXEC);
    if (destination < 0 || !same_regular_inode(destination, &source_status) ||
        !path_still_names_inode(root, destination_path, &source_status) ||
        !directory_path_still_names_inode(root, destination_parent, &parent_status))
        fail(EXIT_DESTINATION, "destination does not name the authorized source inode");
    dprintf(STDOUT_FILENO,
        "{\"authorizationId\":\"%s\",\"outcome\":%u,\"sourceDevice\":%" PRIuMAX
        ",\"sourceInode\":%" PRIuMAX "}\n",
        authorization_id, outcome, (uintmax_t)source_status.st_dev, (uintmax_t)source_status.st_ino);
    _exit(0);
}

int main(int argc, char **argv)
{
    if (argc == 4 && strcmp(argv[1], "--publish-approved") == 0)
        publish_approved_from_stdin(argv[2], argv[3]);
    if (argc == 5 && strcmp(argv[1], "--verify-result") == 0)
        verify_result_command(argv[2], argv[3], argv[4]);
    if (argc == 3 && strcmp(argv[1], "--inspect") == 0)
    {
        const char *path = argv[2];
        if (!safe_absolute_path(path)) fail(EXIT_ARGUMENTS, "inspection path is invalid");
        int root = open("/", O_PATH | O_DIRECTORY | O_CLOEXEC);
        if (root < 0) fail(EXIT_PLATFORM, "cannot open filesystem root");
        int inspected = secure_openat2(root, path + 1, O_PATH | O_NOFOLLOW | O_CLOEXEC);
        if (inspected < 0) fail(errno == ENOSYS ? EXIT_PLATFORM : EXIT_SOURCE, "inspection path cannot be resolved without symlinks");
        struct stat status;
        if (fstat(inspected, &status) != 0 || !S_ISREG(status.st_mode)) fail(EXIT_SOURCE, "inspection path must be a regular file");
        dprintf(STDOUT_FILENO,
            "{\"device\":%" PRIuMAX ",\"inode\":%" PRIuMAX ",\"size\":%" PRIdMAX
            ",\"mtimeSeconds\":%" PRIdMAX ",\"mtimeNanoseconds\":%ld,\"ctimeSeconds\":%" PRIdMAX
            ",\"ctimeNanoseconds\":%ld,\"mode\":%u,\"uid\":%u,\"gid\":%u,\"links\":%" PRIuMAX "}\n",
            (uintmax_t)status.st_dev, (uintmax_t)status.st_ino, (intmax_t)status.st_size,
            (intmax_t)status.st_mtim.tv_sec, status.st_mtim.tv_nsec,
            (intmax_t)status.st_ctim.tv_sec, status.st_ctim.tv_nsec, (unsigned)(status.st_mode & 07777),
            (unsigned)status.st_uid, (unsigned)status.st_gid, (uintmax_t)status.st_nlink);
        _exit(0);
    }

    const char *transfer_mode = getenv("lidarr_transfermode");
    if (transfer_mode == NULL || *transfer_mode == '\0')
        fail(EXIT_CONTEXT, "Lidarr transfer mode is missing");
    if (strcmp(transfer_mode, "HardLinkOrCopy") != 0) return 3;
    if (argc != 3) fail(EXIT_ARGUMENTS, "exactly source and destination arguments are required");
    const char *source_path = argv[1];
    const char *destination_path = argv[2];
    const char *environment_source = getenv("lidarr_sourcepath");
    const char *environment_destination = getenv("lidarr_destinationpath");
    if (environment_source == NULL || environment_destination == NULL ||
        strcmp(source_path, environment_source) != 0 || strcmp(destination_path, environment_destination) != 0)
        fail(EXIT_CONTEXT, "arguments do not match Lidarr environment paths");
    if (!safe_absolute_path(source_path) || !safe_absolute_path(destination_path) ||
        !valid_utf8((const unsigned char *)source_path, strlen(source_path)) ||
        !valid_utf8((const unsigned char *)destination_path, strlen(destination_path)))
        fail(EXIT_ARGUMENTS, "paths must be absolute canonical UTF-8 without control components");
    uint32_t artist_id, album_id, track_id;
    if (!parse_positive_id(getenv("lidarr_artist_id"), &artist_id) ||
        !parse_positive_id(getenv("lidarr_album_id"), &album_id) ||
        !parse_positive_id(getenv("lidarr_trackfile_trackids"), &track_id))
        fail(EXIT_CONTEXT, "positive Lidarr artist, album, and single track IDs are required");

    const char *auth_path = getenv("AIIMPORT_AUTHORIZATION_DIR");
    if (!safe_absolute_path(auth_path) || !valid_utf8((const unsigned char *)auth_path, strlen(auth_path)))
        fail(EXIT_AUTHORIZATION, "AIIMPORT_AUTHORIZATION_DIR is invalid");
    char *destination_copy = strdup(destination_path + 1);
    if (destination_copy == NULL) fail(EXIT_PLATFORM, "memory allocation failed");
    char *separator = strrchr(destination_copy, '/');
    const char *destination_name; const char *destination_parent;
    if (separator == NULL) { destination_parent = "."; destination_name = destination_copy; }
    else { *separator = '\0'; destination_parent = destination_copy; destination_name = separator + 1; }
    if (*destination_name == '\0' || strcmp(destination_name, ".") == 0 || strcmp(destination_name, "..") == 0)
        fail(EXIT_ARGUMENTS, "destination basename is invalid");

    int root = open("/", O_PATH | O_DIRECTORY | O_CLOEXEC);
    if (root < 0) fail(EXIT_PLATFORM, "cannot open filesystem root");
    int source = secure_openat2(root, source_path + 1, O_PATH | O_NOFOLLOW | O_CLOEXEC);
    if (source < 0) fail(errno == ENOSYS ? EXIT_PLATFORM : EXIT_SOURCE, "source cannot be resolved without symlinks");
    struct stat source_status;
    if (fstat(source, &source_status) != 0 || !S_ISREG(source_status.st_mode)) fail(EXIT_SOURCE, "source must be a regular file");
    test_pause("AIIMPORT_TEST_AFTER_SOURCE_OPEN");
    int destination_directory = secure_openat2(root, destination_parent, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
    if (destination_directory < 0) fail(errno == ENOSYS ? EXIT_PLATFORM : EXIT_DESTINATION, "destination parent cannot be resolved without symlinks");
    struct stat destination_parent_status;
    if (fstat(destination_directory, &destination_parent_status) != 0) fail(EXIT_DESTINATION, "cannot inspect destination parent");
    int authfd = open_authorization_directory(auth_path);

    DIR *directory = fdopendir(dup(authfd));
    if (directory == NULL) fail(EXIT_AUTHORIZATION, "cannot enumerate authorization directory");
    approved_record selected; unsigned matches = 0; struct dirent *entry;
    while ((entry = readdir(directory)) != NULL)
    {
        if (strlen(entry->d_name) != 73 || strcmp(entry->d_name + 64, ".approved") != 0) continue;
        int descriptor = openat(authfd, entry->d_name, O_RDONLY | O_NOFOLLOW | O_CLOEXEC);
        if (descriptor < 0) continue;
        approved_record candidate;
        bool valid = parse_approved(descriptor, entry->d_name, &candidate);
        close(descriptor);
        if (!valid || strcmp(candidate.source_path, source_path) != 0 || candidate.artist != artist_id ||
            candidate.album != album_id || candidate.track != track_id ||
            !path_is_within_root(destination_path, candidate.destination_root) ||
            !identity_matches(&candidate, &source_status, false)) continue;
        int claim = -1;
        int claim_state = parse_claim(authfd, &candidate, destination_path, &destination_parent_status, &claim);
        if (claim_state == 1) close(claim);
        if (claim_state < 0) continue;
        if (claim_state == 0 && (candidate.expires <= (uint64_t)time(NULL) || !identity_matches(&candidate, &source_status, true))) continue;
        selected = candidate;
        matches++;
    }
    closedir(directory);
    if (matches != 1) fail(EXIT_AUTHORIZATION, matches == 0 ? "no exact transfer authorization matched" : "multiple transfer authorizations matched");

    int claimfd = create_or_open_claim(authfd, &selected, destination_path, &destination_parent_status);
    if (flock(claimfd, LOCK_EX) != 0) fail(EXIT_AUTHORIZATION, "cannot lock transfer claim");
    if (fstat(source, &source_status) != 0 || !identity_matches(&selected, &source_status, false))
        fail(EXIT_SOURCE, "source identity changed after authorization");
    int existing = openat(destination_directory, destination_name, O_PATH | O_NOFOLLOW | O_CLOEXEC);
    uint32_t existing_outcome = result_outcome(authfd, &selected, &source_status);
    if (existing_outcome != 0)
    {
        if (existing_outcome == 3)
            fail(EXIT_DESTINATION, "durable transfer result records a failed transfer");
        if (existing < 0 || !same_regular_inode(existing, &source_status) ||
            !path_still_names_inode(root, destination_path, &source_status) ||
            !directory_path_still_names_inode(root, destination_parent, &destination_parent_status))
            fail(EXIT_DESTINATION, "committed destination is missing or replaced");
        dprintf(STDOUT_FILENO, "hardlink import: verified committed hardlink\n");
        _exit(0);
    }
    if (existing >= 0)
    {
        if (!same_regular_inode(existing, &source_status) ||
            !path_still_names_inode(root, destination_path, &source_status) ||
            !directory_path_still_names_inode(root, destination_parent, &destination_parent_status))
            fail(EXIT_DESTINATION, "destination exists with a different identity");
        test_pause("AIIMPORT_TEST_BEFORE_EXISTING_RESULT");
        if (!path_still_names_inode(root, destination_path, &source_status) ||
            !directory_path_still_names_inode(root, destination_parent, &destination_parent_status))
            fail(EXIT_DESTINATION, "destination was replaced before durable result publication");
        publish_result(authfd, &selected, &source_status, 2);
        dprintf(STDOUT_FILENO, "hardlink import: reconciled existing authorized hardlink\n");
        _exit(0);
    }
    if (selected.expires <= (uint64_t)time(NULL) || !identity_matches(&selected, &source_status, true))
        fail(EXIT_SOURCE, "source identity changed before authorized hardlink creation");
    test_pause("AIIMPORT_TEST_BEFORE_LINK");
    if (!path_still_names_inode(root, source_path, &source_status))
        fail(EXIT_SOURCE, "source pathname was replaced after authorization");
    if (!directory_path_still_names_inode(root, destination_parent, &destination_parent_status))
        fail(EXIT_DESTINATION, "destination parent pathname was replaced after authorization");
    sigset_t blocked;
    sigemptyset(&blocked); sigaddset(&blocked, SIGHUP); sigaddset(&blocked, SIGINT); sigaddset(&blocked, SIGTERM);
    if (sigprocmask(SIG_BLOCK, &blocked, NULL) != 0) fail(EXIT_PLATFORM, "cannot block termination signals");
    char proc_source[64];
    if (snprintf(proc_source, sizeof(proc_source), "/proc/self/fd/%d", source) >= (int)sizeof(proc_source))
        fail(EXIT_PLATFORM, "source descriptor path overflow");
    bool created_link = false;
    if (linkat(AT_FDCWD, proc_source, destination_directory, destination_name, AT_SYMLINK_FOLLOW) != 0)
    {
        if (errno != EEXIST) fail(EXIT_LINK, "descriptor-bound hardlink creation failed; copy fallback is forbidden");
        existing = openat(destination_directory, destination_name, O_PATH | O_NOFOLLOW | O_CLOEXEC);
        if (existing < 0 || !same_regular_inode(existing, &source_status)) fail(EXIT_DESTINATION, "destination exists with a different identity");
    }
    else created_link = true;
    if (fsync(destination_directory) != 0) fail(EXIT_DESTINATION, "cannot synchronize destination directory");
    test_pause("AIIMPORT_TEST_AFTER_LINK");
    existing = openat(destination_directory, destination_name, O_PATH | O_NOFOLLOW | O_CLOEXEC);
    bool destination_matches = existing >= 0 && same_regular_inode(existing, &source_status);
    bool post_link_valid = fstat(source, &source_status) == 0 &&
        identity_matches(&selected, &source_status, false) &&
        path_still_names_inode(root, source_path, &source_status) &&
        directory_path_still_names_inode(root, destination_parent, &destination_parent_status) &&
        destination_matches && path_still_names_inode(root, destination_path, &source_status);
    if (!post_link_valid)
    {
        if (created_link && destination_matches)
        {
            test_pause("AIIMPORT_TEST_BEFORE_ROLLBACK");
            int rollback_candidate = openat(destination_directory, destination_name, O_PATH | O_NOFOLLOW | O_CLOEXEC);
            if (rollback_candidate < 0 || !same_regular_inode(rollback_candidate, &source_status))
                fail(EXIT_DESTINATION, "created hardlink was replaced after post-link validation failed; replacement was retained");
            close(rollback_candidate);
            test_pause("AIIMPORT_TEST_AFTER_ROLLBACK_CHECK");
            fail(EXIT_DESTINATION, "post-link validation failed; the created hardlink was retained because Linux has no conditional unlink-by-inode primitive");
        }
        fail(EXIT_DESTINATION, created_link
            ? "post-link validation failed; the destination path was retained for inspection"
            : "authorized identity changed while reconciling an existing hardlink");
    }
    publish_result(authfd, &selected, &source_status, 1);
    dprintf(STDOUT_FILENO, "hardlink import: authorized descriptor-bound hardlink verified\n");
    _exit(0);
}
