#pragma once
#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif

/** True when the firmware runs in BoardSim's (or Espressif's) QEMU, false on a real chip. */
bool boardsim_in_qemu(void);

#ifdef __cplusplus
}
#endif
