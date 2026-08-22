#define _GNU_SOURCE

#include <dlfcn.h>
#include <errno.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <unistd.h>

#define CHILD_COUNT 3
#define VALUE_COUNT 8

typedef struct {
    pid_t pid;
    uint32_t values[VALUE_COUNT];
} child_result;

extern uint32_t libcxx_rs_default_next(void *instance)
    __asm__("_ZNSt3__112__rs_defaultclEv");

static int write_all(int fd, const void *buffer, size_t size)
{
    const unsigned char *cursor = buffer;

    while (size != 0) {
        ssize_t written = write(fd, cursor, size);

        if (written > 0) {
            cursor += (size_t)written;
            size -= (size_t)written;
            continue;
        }

        if (written == -1 && errno == EINTR)
            continue;

        return -1;
    }

    return 0;
}

static int read_all(int fd, void *buffer, size_t size)
{
    unsigned char *cursor = buffer;

    while (size != 0) {
        ssize_t received = read(fd, cursor, size);

        if (received > 0) {
            cursor += (size_t)received;
            size -= (size_t)received;
            continue;
        }

        if (received == -1 && errno == EINTR)
            continue;

        return -1;
    }

    return 0;
}

static void report_provider(void)
{
    const char *symbol_name = "_ZNSt3__112__rs_defaultclEv";
    void *symbol = dlsym(RTLD_DEFAULT, symbol_name);
    Dl_info information = {0};

    if (symbol == NULL) {
        fprintf(stderr, "dlsym failed for %s: %s\n", symbol_name, dlerror());
        exit(EXIT_FAILURE);
    }

    if (dladdr(symbol, &information) == 0 || information.dli_fname == NULL) {
        fprintf(stderr, "dladdr could not identify the symbol provider\n");
        exit(EXIT_FAILURE);
    }

    printf("resolved-provider: %s\n", information.dli_fname);
}

static void print_result(const child_result *result)
{
    size_t index;

    printf("child-pid=%ld:", (long)result->pid);
    for (index = 0; index < VALUE_COUNT; ++index)
        printf(" %08x", result->values[index]);
    putchar('\n');
}

int main(int argc, char **argv)
{
    int expect_same;
    int pipes[CHILD_COUNT][2];
    pid_t children[CHILD_COUNT];
    child_result results[CHILD_COUNT];
    uint32_t warmup[4];
    size_t index;
    int all_same = 1;

    if (argc != 2 || (strcmp(argv[1], "--expect-same") != 0 &&
                      strcmp(argv[1], "--expect-different") != 0)) {
        fprintf(stderr, "usage: %s --expect-same|--expect-different\n",
                argv[0]);
        return EXIT_FAILURE;
    }

    expect_same = strcmp(argv[1], "--expect-same") == 0;
    report_provider();

    /* Initialise and advance libc++ state before forking, like CowMaster. */
    for (index = 0; index < sizeof(warmup) / sizeof(warmup[0]); ++index)
        warmup[index] = libcxx_rs_default_next(NULL);

    for (index = 0; index < CHILD_COUNT; ++index) {
        size_t value_index;

        if (pipe(pipes[index]) != 0) {
            perror("pipe");
            return EXIT_FAILURE;
        }

        children[index] = fork();
        if (children[index] == -1) {
            perror("fork");
            return EXIT_FAILURE;
        }

        if (children[index] == 0) {
            child_result result = {.pid = getpid()};

            (void)close(pipes[index][0]);
            for (value_index = 0; value_index < VALUE_COUNT; ++value_index)
                result.values[value_index] = libcxx_rs_default_next(NULL);

            if (write_all(pipes[index][1], &result, sizeof(result)) != 0)
                _exit(2);

            (void)close(pipes[index][1]);
            _exit(0);
        }

        (void)close(pipes[index][1]);
    }

    for (index = 0; index < CHILD_COUNT; ++index) {
        int status;

        if (read_all(pipes[index][0], &results[index],
                     sizeof(results[index])) != 0) {
            fprintf(stderr, "failed to read child %zu result\n", index);
            return EXIT_FAILURE;
        }
        (void)close(pipes[index][0]);

        if (waitpid(children[index], &status, 0) == -1 ||
            !WIFEXITED(status) || WEXITSTATUS(status) != 0) {
            fprintf(stderr, "child %zu failed\n", index);
            return EXIT_FAILURE;
        }

        print_result(&results[index]);
    }

    for (index = 1; index < CHILD_COUNT; ++index) {
        if (memcmp(results[0].values, results[index].values,
                   sizeof(results[0].values)) != 0) {
            all_same = 0;
            break;
        }
    }

    printf("forked-sequences: %s\n", all_same ? "identical" : "different");

    if (expect_same != all_same) {
        fprintf(stderr, "result did not match %s\n", argv[1]);
        return EXIT_FAILURE;
    }

    return EXIT_SUCCESS;
}
