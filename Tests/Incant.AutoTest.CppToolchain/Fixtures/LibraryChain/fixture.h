#pragma once

#if defined(_WIN32)
#if defined(INCANT_FIXTURE_BUILD_SHARED)
#define INCANT_FIXTURE_EXPORT __declspec(dllexport)
#else
#define INCANT_FIXTURE_EXPORT __declspec(dllimport)
#endif
#else
#define INCANT_FIXTURE_EXPORT __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

int static_c_value(void);
int static_c_extra(void);
int static_cpp_value(void);
INCANT_FIXTURE_EXPORT int shared_value(void);

#ifdef __cplusplus
}
#endif
