#define _GNU_SOURCE

#include <errno.h>
#include <stdatomic.h>
#include <stddef.h>
#include <stdint.h>
#include <sys/syscall.h>
#include <time.h>
#include <unistd.h>

/*
 * ABI-specific interposer for the libc++ bundled with the HoN Linux server.
 *
 * That libc++ implements std::__1::__rs_default::operator()() with a
 * function-local mt19937 seeded with the fixed default seed (5489). CowMaster
 * forks its slaves after that state exists, so every child inherits the same
 * generator state. Interposing only operator() leaves libc++ object lifetime
 * and layout untouched while giving each process an independent stream.
 *
 * No pthread lock is used here: a lock held by another thread at fork would be
 * inherited permanently locked by the child. The small atomic state machine
 * below recognises an in-progress initialisation belonging to a different PID
 * and safely replaces it in the child.
 */

#define INITIALISING_BIT (UINT64_C(1) << 63)
#define SPLITMIX_INCREMENT UINT64_C(0x9e3779b97f4a7c15)

static _Atomic uint64_t process_state;
static _Atomic uint64_t sequence_state;

static uint64_t mix64(uint64_t value)
{
    value = (value ^ (value >> 30)) * UINT64_C(0xbf58476d1ce4e5b9);
    value = (value ^ (value >> 27)) * UINT64_C(0x94d049bb133111eb);
    return value ^ (value >> 31);
}

static uint64_t current_pid(void)
{
    return (uint64_t)syscall(SYS_getpid);
}

static int fill_from_getrandom(void *buffer, size_t size)
{
    unsigned char *cursor = buffer;

    while (size != 0) {
        long result = syscall(SYS_getrandom, cursor, size, 0);

        if (result > 0) {
            cursor += (size_t)result;
            size -= (size_t)result;
            continue;
        }

        if (result == -1 && errno == EINTR)
            continue;

        return -1;
    }

    return 0;
}

static uint64_t fallback_seed(uint64_t pid)
{
    struct timespec realtime = {0};
    struct timespec monotonic = {0};
    uintptr_t stack_address = (uintptr_t)&pid;
    uintptr_t code_address = (uintptr_t)&fallback_seed;

    (void)syscall(SYS_clock_gettime, CLOCK_REALTIME, &realtime);
    (void)syscall(SYS_clock_gettime, CLOCK_MONOTONIC, &monotonic);

    return mix64(pid ^ ((uint64_t)realtime.tv_sec << 32) ^
                 (uint64_t)realtime.tv_nsec ^
                 ((uint64_t)monotonic.tv_sec << 17) ^
                 (uint64_t)monotonic.tv_nsec ^ (uint64_t)stack_address ^
                 (uint64_t)code_address);
}

static uint64_t new_process_seed(uint64_t pid)
{
    uint64_t seed;

    if (fill_from_getrandom(&seed, sizeof(seed)) != 0)
        seed = fallback_seed(pid);

    /* Avoid an all-zero diagnostic value even though SplitMix64 permits it. */
    return seed != 0 ? seed : mix64(pid ^ SPLITMIX_INCREMENT);
}

static void wait_for_initialiser(void)
{
#if defined(__x86_64__) || defined(__i386__)
    __asm__ volatile("pause" ::: "memory");
#else
    atomic_signal_fence(memory_order_seq_cst);
#endif
}

static void ensure_process_stream(uint64_t pid)
{
    const uint64_t initialising = INITIALISING_BIT | pid;

    for (;;) {
        uint64_t observed =
            atomic_load_explicit(&process_state, memory_order_acquire);

        if (observed == pid)
            return;

        if (observed == initialising) {
            wait_for_initialiser();
            continue;
        }

        if (!atomic_compare_exchange_weak_explicit(
                &process_state, &observed, initialising,
                memory_order_acq_rel, memory_order_acquire))
            continue;

        atomic_store_explicit(&sequence_state, new_process_seed(pid),
                              memory_order_relaxed);
        atomic_store_explicit(&process_state, pid, memory_order_release);
        return;
    }
}

/*
 * Exact mangled name exported by the deployed libc++.so.1. The real libc++
 * result_type is unsigned int, so returning uint32_t preserves the x86-64
 * System V ABI (EAX, zero-extended into RAX). The implicit C++ `this` pointer
 * arrives as the first argument and is deliberately unused.
 */
__attribute__((visibility("default")))
uint32_t hon_libcxx_rs_default_next(void *instance)
    __asm__("_ZNSt3__112__rs_defaultclEv");

uint32_t hon_libcxx_rs_default_next(void *instance)
{
    uint64_t pid = current_pid();
    uint64_t value;

    (void)instance;
    ensure_process_stream(pid);
    value = atomic_fetch_add_explicit(&sequence_state, SPLITMIX_INCREMENT,
                                      memory_order_relaxed);
    return (uint32_t)mix64(value);
}
