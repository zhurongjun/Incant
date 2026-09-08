#include "fixture.h"

#include <cstdio>

int main()
{
    if (shared_value() + static_c_extra() != 42)
    {
        return 1;
    }

    std::puts("INCANT_TOOLCHAIN_CPP_OK");
    return 0;
}
