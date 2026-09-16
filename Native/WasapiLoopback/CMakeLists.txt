cmake_minimum_required(VERSION 3.20)
project(wasapi_loopback LANGUAGES CXX)

add_library(wasapi_loopback SHARED wasapi_loopback.cpp)
target_compile_features(wasapi_loopback PRIVATE cxx_std_17)
target_compile_definitions(wasapi_loopback PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
target_compile_options(wasapi_loopback PRIVATE /W4 /EHsc /O2)
target_link_libraries(wasapi_loopback PRIVATE ole32 uuid)
set_property(TARGET wasapi_loopback PROPERTY
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

