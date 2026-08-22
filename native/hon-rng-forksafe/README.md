# HoN fork-safe libc++ shuffle RNG interposer

This native compatibility library targets the bundled libc++ ABI used by the
HoN 4.14.1.2 Linux match-server distribution. It interposes:

```text
std::__1::__rs_default::operator()()
_ZNSt3__112__rs_defaultclEv
```

The bundled implementation keeps a function-local `mt19937` with its fixed
default seed. CowMaster initialises that generator before forking, causing each
slave to inherit an identical shuffle stream. The interposer recognises PID
changes and supplies an independently seeded, process-local SplitMix64 stream.
It deliberately avoids pthread locks because a child could inherit a lock held
by another thread at the instant of `fork(2)`.

## Build and test

The test harness links against the authoritative deployed libc++, warms its
generator before forking, and proves that the unmodified implementation repeats
while the interposed implementation does not:

```sh
make build/libhon-rng-forksafe.so
make inspect
make test DEPLOYED_LIBCXX_DIR=/path/to/las/libs-x86_64
```

Building and inspecting the shim does not require a HoN installation. Building
the test harness requires `DEPLOYED_LIBCXX_DIR` to identify the authoritative
staged LAS `libs-x86_64` directory containing `libc++.so.1` and
`libc++abi.so.1`; the Makefile deliberately has no host-specific default.

## LAS packaging and COMPEL activation

Publish only the built library at this path in a compatible LAS distribution:

```text
compatibility/libhon-rng-forksafe.so
```

Include the file in `las/manifest.json`. COMPEL detects that path after the LAS
synchronisation has completed and adds its fully qualified path to CowMaster's
child-only `LD_PRELOAD`. CowMaster's forked slaves inherit the mapping, while
COMPEL itself does not load the library. Omitting the file from a future LAS
distribution disables the ABI-specific workaround without a COMPEL change.
