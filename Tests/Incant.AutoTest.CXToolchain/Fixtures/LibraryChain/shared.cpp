#define INCANT_FIXTURE_BUILD_SHARED
#include "fixture.h"

extern "C" INCANT_FIXTURE_EXPORT int shared_value(void)
{
    return static_c_value() + static_cpp_value();
}
