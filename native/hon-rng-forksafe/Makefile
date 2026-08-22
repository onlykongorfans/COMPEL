CC ?= clang
DEPLOYED_LIBCXX_DIR ?=
BUILD_DIR := build
SHIM := $(BUILD_DIR)/libhon-rng-forksafe.so
HARNESS := $(BUILD_DIR)/rng-fork-test

CFLAGS := -std=c11 -O2 -Wall -Wextra -Werror -fno-strict-aliasing
HARDENING_LDFLAGS := -Wl,-z,defs -Wl,-z,relro -Wl,-z,now

.PHONY: all clean inspect test

all: $(SHIM) $(HARNESS)

$(BUILD_DIR):
	mkdir -p $@

$(SHIM): src/hon_rng_forksafe.c | $(BUILD_DIR)
	$(CC) $(CFLAGS) -fPIC -fvisibility=hidden -shared \
		$(HARDENING_LDFLAGS) -Wl,-soname,libhon-rng-forksafe.so \
		-o $@ $<

$(HARNESS): tests/rng_fork_test.c | $(BUILD_DIR)
	@test -n "$(strip $(DEPLOYED_LIBCXX_DIR))" || { echo 'DEPLOYED_LIBCXX_DIR is required to build the test harness (for example: make test DEPLOYED_LIBCXX_DIR=/path/to/las/libs-x86_64)' >&2; exit 1; }
	@test -f '$(DEPLOYED_LIBCXX_DIR)/libc++.so.1' || { echo 'libc++.so.1 was not found beneath DEPLOYED_LIBCXX_DIR=$(DEPLOYED_LIBCXX_DIR)' >&2; exit 1; }
	@test -f '$(DEPLOYED_LIBCXX_DIR)/libc++abi.so.1' || { echo 'libc++abi.so.1 was not found beneath DEPLOYED_LIBCXX_DIR=$(DEPLOYED_LIBCXX_DIR)' >&2; exit 1; }
	$(CC) $(CFLAGS) $(HARDENING_LDFLAGS) \
		-Wl,-rpath,'$(DEPLOYED_LIBCXX_DIR)' \
		-L'$(DEPLOYED_LIBCXX_DIR)' -o $@ $< \
		-l:libc++.so.1 -l:libc++abi.so.1 -ldl

inspect: $(SHIM)
	readelf -Ws $(SHIM) | c++filt | grep '__rs_default::operator()'
	readelf -d $(SHIM) | grep NEEDED

test: all
	$(HARNESS) --expect-same
	LD_PRELOAD=$(abspath $(SHIM)) $(HARNESS) --expect-different

clean:
	rm -rf $(BUILD_DIR)
