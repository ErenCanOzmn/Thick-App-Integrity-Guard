#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "integrity_guard.h"

/* Development smoke client. Production hosts construct config from trusted settings. */
int main(int argc, char **argv) {
    if (argc != 3) { fprintf(stderr, "usage: sample config.json protected-filename\n"); return 2; }
    if (ig_abi_version() != 1) return 2;
    FILE *file = fopen(argv[1], "rb");
    if (!file) return 2;
    uint8_t config[131073];
    size_t count = fread(config, 1, sizeof(config), file);
    int read_error = ferror(file);
    fclose(file);
    if (read_error || count == 0 || count > 131072 || strlen(argv[2]) > 128) return 2;
    ig_handle handle = 0;
    int32_t code = ig_open(config, (uint32_t)count, &handle);
    if (code != IG_OK) { fprintf(stderr, "open: %d\n", code); return 1; }
    uint8_t *bytes = NULL;
    code = ig_check(handle, IG_BEFORE_OPERATION);
    if (code != IG_OK) goto done;
    uint32_t size = 0;
    code = ig_copy_file(handle, (const uint8_t *)argv[2], (uint32_t)strlen(argv[2]), NULL, 0, &size);
    if (code != IG_OK && code != IG_BUFFER_TOO_SMALL) goto done;
    if (size > 33554432) { code = IG_INTERNAL_ERROR; goto done; }
    bytes = (uint8_t *)malloc(size ? size : 1);
    if (!bytes) { code = IG_RESOURCE_LIMIT; goto done; }
    code = ig_copy_file(handle, (const uint8_t *)argv[2], (uint32_t)strlen(argv[2]), bytes, size, &size);
    if (code == IG_OK) printf("Verified snapshot: %u bytes\n", size);
    /* Consume bytes here, with an explicit length. Never reopen the original file. */
done:
    free(bytes); /* caller allocator, not a foreign runtime allocator */
    ig_close(handle);
    if (code != IG_OK) fprintf(stderr, "verification/copy: %d\n", code);
    return code == IG_OK ? 0 : 1;
}
