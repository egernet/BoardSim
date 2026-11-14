/*
 * Wi-Fi for firmware running in BoardSim.
 *
 * The ESP32's radio is driven by closed libraries and cannot be emulated, so
 * the esp_wifi API is wrapped at link time. On a real chip every wrapper calls
 * ESP-IDF's own function. In QEMU the station interface is backed by the
 * emulated OpenCores Ethernet MAC instead: connecting succeeds, the usual
 * WIFI_EVENT_* and IP_EVENT_STA_GOT_IP events arrive, and QEMU's NAT (slirp)
 * gives the firmware network and internet access through the host.
 *
 * SPDX-License-Identifier: MIT
 */
#include <string.h>
#include "sdkconfig.h"
#include "esp_log.h"
#include "esp_wifi.h"
#include "esp_wifi_default.h"
#include "esp_netif.h"
#include "esp_event.h"
#include "esp_eth.h"
#include "esp_eth_mac_openeth.h"
#include "esp_mac.h"
#include "boardsim.h"

static const char *TAG = "boardsim.wifi";

/* QEMU answers "QEMU" from an otherwise unused system register. */
#define BOARDSIM_ORIGIN_REG   0x600263F8
#define BOARDSIM_ORIGIN_QEMU  0x51454d55

void boardsim_wifi_linked(void) {}

bool boardsim_in_qemu(void)
{
    static int cached = -1;
    if (cached < 0) {
        cached = (*(volatile uint32_t *)BOARDSIM_ORIGIN_REG) == BOARDSIM_ORIGIN_QEMU;
    }
    return cached;
}

#if CONFIG_BOARDSIM_WIFI
#define EMULATE() (boardsim_in_qemu())
#else
#define EMULATE() (false)
#endif

static wifi_mode_t s_mode = WIFI_MODE_NULL;
static wifi_config_t s_sta_config;
static bool s_started, s_connected, s_eth_started;
static esp_netif_t *s_sta_netif;
static esp_eth_handle_t s_eth;

static const char *ssid(void)
{
    return s_sta_config.sta.ssid[0] ? (const char *)s_sta_config.sta.ssid : CONFIG_BOARDSIM_WIFI_SSID;
}

static void post(int32_t id, void *data, size_t size)
{
    esp_event_post(WIFI_EVENT, id, data, size, portMAX_DELAY);
}

static esp_err_t ensure_ethernet(void)
{
    if (s_eth) {
        return ESP_OK;
    }
    eth_mac_config_t mac_config = ETH_MAC_DEFAULT_CONFIG();
    eth_phy_config_t phy_config = ETH_PHY_DEFAULT_CONFIG();
    phy_config.autonego_timeout_ms = 100;
    esp_eth_mac_t *mac = esp_eth_mac_new_openeth(&mac_config);
    esp_eth_phy_t *phy = esp_eth_phy_new_dp83848(&phy_config);
    esp_eth_config_t config = ETH_DEFAULT_CONFIG(mac, phy);
    esp_err_t err = esp_eth_driver_install(&config, &s_eth);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "emulated Ethernet not available: %s", esp_err_to_name(err));
        return err;
    }
    if (s_sta_netif) {
        esp_netif_attach(s_sta_netif, esp_eth_new_netif_glue(s_eth));
    }
    return ESP_OK;
}

static void on_got_ip(void *arg, esp_event_base_t base, int32_t id, void *data)
{
    ip_event_got_ip_t *ev = (ip_event_got_ip_t *)data;
    if (ev->esp_netif == s_sta_netif) {
        ESP_LOGI(TAG, "BoardSim: got IP " IPSTR " (gateway " IPSTR ", the host is 10.0.2.2)",
                 IP2STR(&ev->ip_info.ip), IP2STR(&ev->ip_info.gw));
    }
}

static esp_err_t emulated_connect(void)
{
    static bool logging;
    if (!logging) {
        logging = true;
        esp_event_handler_register(IP_EVENT, IP_EVENT_STA_GOT_IP, on_got_ip, NULL);
    }
    if (ensure_ethernet() != ESP_OK) {
        return ESP_FAIL;
    }
    if (!s_eth_started) {
        esp_eth_start(s_eth);
        s_eth_started = true;
    }
    if (!s_connected) {
        s_connected = true;
        wifi_event_sta_connected_t ev = { .channel = 6, .authmode = WIFI_AUTH_WPA2_PSK, .aid = 1 };
        memcpy(ev.ssid, ssid(), strnlen(ssid(), sizeof(ev.ssid)));
        ev.ssid_len = strnlen(ssid(), sizeof(ev.ssid));
        memcpy(ev.bssid, "\x02\x42\x53\x49\x4d\x01", 6);   /* 02:42:53:49:4d:01 ("BSIM") */
        ESP_LOGI(TAG, "BoardSim: connected to \"%s\" (emulated Wi-Fi over QEMU's network)", ssid());
        post(WIFI_EVENT_STA_CONNECTED, &ev, sizeof(ev));
    }
    return ESP_OK;
}

/* ---- wrappers ------------------------------------------------------------ */

#define WRAP(ret, name, params, args, ...)               \
    ret __real_##name params;                            \
    ret __wrap_##name params                             \
    {                                                    \
        if (!EMULATE()) {                                \
            return __real_##name args;                   \
        }                                                \
        __VA_ARGS__                                      \
    }

WRAP(esp_err_t, esp_wifi_init, (const wifi_init_config_t *config), (config),
     ESP_LOGI(TAG, "BoardSim: Wi-Fi is emulated on the emulated Ethernet");
     return ESP_OK;)

WRAP(esp_err_t, esp_wifi_deinit, (void), (), return ESP_OK;)

WRAP(esp_err_t, esp_wifi_set_mode, (wifi_mode_t mode), (mode), s_mode = mode; return ESP_OK;)

WRAP(esp_err_t, esp_wifi_get_mode, (wifi_mode_t *mode), (mode), if (mode) *mode = s_mode; return ESP_OK;)

WRAP(esp_err_t, esp_wifi_start, (void), (),
     s_started = true;
     post(WIFI_EVENT_STA_START, NULL, 0);
#if CONFIG_BOARDSIM_WIFI_AUTOCONNECT
     emulated_connect();
#endif
     return ESP_OK;)

WRAP(esp_err_t, esp_wifi_stop, (void), (),
     if (s_eth_started) { esp_eth_stop(s_eth); s_eth_started = false; }
     s_connected = false;
     s_started = false;
     post(WIFI_EVENT_STA_STOP, NULL, 0);
     return ESP_OK;)

WRAP(esp_err_t, esp_wifi_connect, (void), (), return s_started ? emulated_connect() : ESP_ERR_WIFI_NOT_STARTED;)

WRAP(esp_err_t, esp_wifi_disconnect, (void), (),
     if (s_connected) {
         s_connected = false;
         if (s_eth_started) { esp_eth_stop(s_eth); s_eth_started = false; }
         wifi_event_sta_disconnected_t ev = { .reason = WIFI_REASON_ASSOC_LEAVE };
         post(WIFI_EVENT_STA_DISCONNECTED, &ev, sizeof(ev));
     }
     return ESP_OK;)

WRAP(esp_err_t, esp_wifi_set_config, (wifi_interface_t interface, wifi_config_t *conf), (interface, conf),
     if (interface == WIFI_IF_STA && conf) s_sta_config = *conf;
     return ESP_OK;)

WRAP(esp_err_t, esp_wifi_get_config, (wifi_interface_t interface, wifi_config_t *conf), (interface, conf),
     if (conf) *conf = s_sta_config;
     return ESP_OK;)

WRAP(esp_err_t, esp_wifi_set_storage, (wifi_storage_t storage), (storage), return ESP_OK;)
WRAP(esp_err_t, esp_wifi_set_ps, (wifi_ps_type_t type), (type), return ESP_OK;)
WRAP(esp_err_t, esp_wifi_get_ps, (wifi_ps_type_t *type), (type), if (type) *type = WIFI_PS_NONE; return ESP_OK;)
WRAP(esp_err_t, esp_wifi_set_protocol, (wifi_interface_t ifx, uint8_t bitmap), (ifx, bitmap), return ESP_OK;)
WRAP(esp_err_t, esp_wifi_set_bandwidth, (wifi_interface_t ifx, wifi_bandwidth_t bw), (ifx, bw), return ESP_OK;)
WRAP(esp_err_t, esp_wifi_set_country, (const wifi_country_t *country), (country), return ESP_OK;)
WRAP(esp_err_t, esp_wifi_set_max_tx_power, (int8_t power), (power), return ESP_OK;)
WRAP(esp_err_t, esp_wifi_set_inactive_time, (wifi_interface_t ifx, uint16_t sec), (ifx, sec), return ESP_OK;)
WRAP(esp_err_t, esp_wifi_get_mac, (wifi_interface_t ifx, uint8_t mac[6]), (ifx, mac),
     return esp_read_mac(mac, ifx == WIFI_IF_AP ? ESP_MAC_WIFI_SOFTAP : ESP_MAC_WIFI_STA);)

/* Scanning finds one network: BoardSim. */
WRAP(esp_err_t, esp_wifi_scan_start, (const wifi_scan_config_t *config, bool block), (config, block),
     wifi_event_sta_scan_done_t ev = { .status = 0, .number = 1, .scan_id = 1 };
     post(WIFI_EVENT_SCAN_DONE, &ev, sizeof(ev));
     return ESP_OK;)
WRAP(esp_err_t, esp_wifi_scan_stop, (void), (), return ESP_OK;)
WRAP(esp_err_t, esp_wifi_clear_ap_list, (void), (), return ESP_OK;)
WRAP(esp_err_t, esp_wifi_scan_get_ap_num, (uint16_t *number), (number), if (number) *number = 1; return ESP_OK;)

static void fill_ap(wifi_ap_record_t *ap)
{
    memset(ap, 0, sizeof(*ap));
    memcpy(ap->ssid, ssid(), strnlen(ssid(), sizeof(ap->ssid) - 1));
    memcpy(ap->bssid, "\x02\x42\x53\x49\x4d\x01", 6);
    ap->primary = 6;
    ap->rssi = -42;
    ap->authmode = WIFI_AUTH_WPA2_PSK;
    ap->phy_11b = ap->phy_11g = ap->phy_11n = 1;
}

WRAP(esp_err_t, esp_wifi_scan_get_ap_records, (uint16_t *number, wifi_ap_record_t *records), (number, records),
     if (number && *number > 0 && records) { fill_ap(&records[0]); *number = 1; }
     return ESP_OK;)

WRAP(esp_err_t, esp_wifi_sta_get_ap_info, (wifi_ap_record_t *ap), (ap),
     if (!s_connected) return ESP_ERR_WIFI_NOT_CONNECT;
     if (ap) fill_ap(ap);
     return ESP_OK;)

/* The station interface: Wi-Fi's own IP events (IP_EVENT_STA_GOT_IP), on the Ethernet stack. */
WRAP(esp_netif_t *, esp_netif_create_default_wifi_sta, (void), (),
     esp_netif_config_t cfg = {
         .base = ESP_NETIF_BASE_DEFAULT_WIFI_STA,
         .driver = NULL,
         .stack = ESP_NETIF_NETSTACK_DEFAULT_ETH,
     };
     s_sta_netif = esp_netif_new(&cfg);
     if (s_eth) esp_netif_attach(s_sta_netif, esp_eth_new_netif_glue(s_eth));
     else ensure_ethernet();
     return s_sta_netif;)

/* An access point cannot be emulated; the interface exists so the code runs on. */
WRAP(esp_netif_t *, esp_netif_create_default_wifi_ap, (void), (),
     esp_netif_config_t cfg = {
         .base = ESP_NETIF_BASE_DEFAULT_WIFI_AP,
         .driver = NULL,
         .stack = ESP_NETIF_NETSTACK_DEFAULT_ETH,
     };
     ESP_LOGW(TAG, "BoardSim: soft-AP is not emulated; only the station gets a network");
     return esp_netif_new(&cfg);)

/* esp_netif_create_wifi() with the app's own interface config (ESP-IDF's examples use it). */
WRAP(esp_netif_t *, esp_netif_create_wifi, (wifi_interface_t wifi_if, const esp_netif_inherent_config_t *config), (wifi_if, config),
     esp_netif_config_t cfg = {
         .base = config,
         .driver = NULL,
         .stack = ESP_NETIF_NETSTACK_DEFAULT_ETH,
     };
     esp_netif_t *netif = esp_netif_new(&cfg);
     if (wifi_if == WIFI_IF_STA) {
         s_sta_netif = netif;
         if (s_eth) esp_netif_attach(s_sta_netif, esp_eth_new_netif_glue(s_eth));
         else ensure_ethernet();
     }
     return netif;)

/* The Ethernet glue already drives the station interface. */
WRAP(esp_err_t, esp_wifi_set_default_wifi_sta_handlers, (void), (), return ESP_OK;)
WRAP(esp_err_t, esp_wifi_clear_default_wifi_driver_and_handlers, (void *esp_netif), (esp_netif), return ESP_OK;)
