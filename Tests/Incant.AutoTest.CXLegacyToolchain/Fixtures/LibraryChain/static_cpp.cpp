#include "fixture.h"

#include <array>

extern "C" int static_cpp_value(void)
{
    constexpr std::array<int, 3> values = { 10, 10, 11 };
    return values[0] + values[1] + values[2];
}
