#include "fixture.h"

#include <stdio.h>

int main(void)
{
    if (static_c_value() != 10)
    {
        return 1;
    }

    puts("INCANT_TOOLCHAIN_C_OK");
    return 0;
}
