/* SPDX-License-Identifier: BSD-3-Clause */
#ifndef CHDMAN_TEST_SHA1_H
#define CHDMAN_TEST_SHA1_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct test_sha1 {
	uint32_t h[5];
	uint64_t length;
	uint8_t buf[64];
	size_t used;
} test_sha1;

void test_sha1_init(test_sha1 *s);
void test_sha1_update(test_sha1 *s, const void *data, size_t len);
void test_sha1_final_hex(test_sha1 *s, char out[41]);

#ifdef __cplusplus
}
#endif

#endif
