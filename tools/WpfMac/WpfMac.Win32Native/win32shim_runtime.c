// The hand-written half of libwin32shim: what the managed side calls to read the export table and fill
// slots, and the common path every not-yet-implemented export falls into. The stubs and tables are
// generated (win32shim-gen); see Generator.cs.

#include <stdint.h>
#include <string.h>

extern void *wpfmac_slots[];
extern void *const wpfmac_defaults[];
extern const int wpfmac_export_count;
extern const int wpfmac_module_count;
extern const char *const wpfmac_module_names[];
extern const char *const wpfmac_export_names[];
extern const unsigned short wpfmac_export_modules[];
extern const unsigned char wpfmac_export_failures[];

// int64_t handler(int32_t exportIndex): returns what the unimplemented function returns.
typedef int64_t (*wpfmac_unimplemented_handler)(int32_t);
wpfmac_unimplemented_handler wpfmac_unimplemented_target;

__attribute__((visibility("default"))) int wpfmac_shim_export_count(void) { return wpfmac_export_count; }
__attribute__((visibility("default"))) int wpfmac_shim_module_count(void) { return wpfmac_module_count; }
__attribute__((visibility("default"))) const char *wpfmac_shim_module_name(int i) { return i >= 0 && i < wpfmac_module_count ? wpfmac_module_names[i] : 0; }
__attribute__((visibility("default"))) const char *wpfmac_shim_export_name(int i) { return i >= 0 && i < wpfmac_export_count ? wpfmac_export_names[i] : 0; }
__attribute__((visibility("default"))) int wpfmac_shim_export_module(int i) { return i >= 0 && i < wpfmac_export_count ? wpfmac_export_modules[i] : -1; }
__attribute__((visibility("default"))) int wpfmac_shim_export_failure(int i) { return i >= 0 && i < wpfmac_export_count ? wpfmac_export_failures[i] : -1; }

// Points export i at fn; NULL restores the default (log once, return the failure value).
__attribute__((visibility("default"))) int wpfmac_shim_set_slot(int i, void *fn)
{
    if (i < 0 || i >= wpfmac_export_count) return 0;
    __atomic_store_n(&wpfmac_slots[i], fn ? fn : wpfmac_defaults[i], __ATOMIC_RELEASE);
    return 1;
}

__attribute__((visibility("default"))) void wpfmac_shim_set_unimplemented_handler(wpfmac_unimplemented_handler handler)
{
    __atomic_store_n(&wpfmac_unimplemented_target, handler, __ATOMIC_RELEASE);
}

// Reached from every default thunk with the export's index in w17 (see the stubs). Tail-calls the
// managed handler with the index as its only argument, so the handler's return value lands in x0 for
// the original caller. Before a handler is installed it returns 0.
__attribute__((naked)) void wpfmac_unimplemented(void)
{
    __asm__ volatile(
        "adrp x16, _wpfmac_unimplemented_target@PAGE\n"
        "ldr  x16, [x16, _wpfmac_unimplemented_target@PAGEOFF]\n"
        "cbz  x16, 1f\n"
        "mov  w0, w17\n"
        "br   x16\n"
        "1:\n"
        "mov  x0, #0\n"
        "ret\n");
}
