# boardsim_wifi

Wi-Fi for ESP-IDF firmware running in [BoardSim](../../../README.md).

The ESP32's radio is driven by closed libraries and cannot be emulated. This
component wraps the `esp_wifi` API at link time:

- **On a real chip** every call goes straight to ESP-IDF's own Wi-Fi. Same
  binary, no difference.
- **In BoardSim** (detected at run time) the station is backed by QEMU's
  emulated Ethernet: `esp_wifi_connect()` succeeds, the usual
  `WIFI_EVENT_STA_CONNECTED` and `IP_EVENT_STA_GOT_IP` events arrive, a scan
  finds one network ("BoardSim"), and QEMU's NAT gives the firmware the
  network and the internet through the computer. The address is 10.0.2.15;
  the computer is 10.0.2.2.

## Use it

```bash
idf.py -D EXTRA_COMPONENT_DIRS=/path/to/BoardSim/firmware/components build
```

or copy `boardsim_wifi` into your project's `components/`. If your project
sets `idf_build_set_property(MINIMAL_BUILD ON)` (newer ESP-IDF examples do),
add `boardsim_wifi` to your main component's `REQUIRES`, or turn
`MINIMAL_BUILD` off, so it gets linked.

Settings (`idf.py menuconfig` → BoardSim Wi-Fi): connect automatically when
Wi-Fi starts (default on, for firmware that waits for saved credentials),
and the network name reported.

Verified with ESP-IDF 5.5: the Julelys firmware, and Espressif's
`protocols/http_request` example fetching example.com unchanged.

## Limits

Station mode only (a soft-AP interface is created but gets no network), no
ESP-NOW, no RSSI changes, no roaming. Incoming connections need a port
forward in QEMU; BoardSim forwards UDP 2412 from `127.0.0.1:12412`.
