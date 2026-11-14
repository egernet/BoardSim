idf_component_register(
    SRCS "boardsim_wifi.c"
    INCLUDE_DIRS "include"
    REQUIRES esp_wifi esp_eth esp_netif esp_event esp_hw_support)

# Every call to these goes through boardsim_wifi.c first. On real hardware it
# passes straight on to ESP-IDF's own function; in BoardSim it emulates Wi-Fi.
set(BOARDSIM_WIFI_WRAPPED
    esp_wifi_init esp_wifi_deinit esp_wifi_set_mode esp_wifi_get_mode
    esp_wifi_start esp_wifi_stop esp_wifi_connect esp_wifi_disconnect
    esp_wifi_set_config esp_wifi_get_config esp_wifi_set_storage
    esp_wifi_set_ps esp_wifi_get_ps esp_wifi_scan_start esp_wifi_scan_stop
    esp_wifi_scan_get_ap_num esp_wifi_scan_get_ap_records esp_wifi_clear_ap_list
    esp_wifi_sta_get_ap_info esp_wifi_set_protocol esp_wifi_set_bandwidth
    esp_wifi_set_country esp_wifi_get_mac esp_wifi_set_max_tx_power
    esp_wifi_set_inactive_time
    esp_netif_create_default_wifi_sta esp_netif_create_default_wifi_ap
    esp_netif_create_wifi esp_wifi_set_default_wifi_sta_handlers
    esp_wifi_clear_default_wifi_driver_and_handlers)
foreach(fn ${BOARDSIM_WIFI_WRAPPED})
    target_link_libraries(${COMPONENT_LIB} INTERFACE "-Wl,--wrap=${fn}")
endforeach()
# Make sure the wrappers are linked even though only the wrapped names are referenced.
target_link_libraries(${COMPONENT_LIB} INTERFACE "-u boardsim_wifi_linked")
