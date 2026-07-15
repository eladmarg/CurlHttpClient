# Custom triplet: x64, fully static (libs + CRT), forced MSVC v142 toolset.
#
# v142 (VS 2019 Build Tools 14.29) produces binaries that run on Windows 7 SP1+
# including Windows Server 2012 R2, which is the deployment target of this
# project. Do not bump the toolset without re-validating the OS floor.
set(VCPKG_TARGET_ARCHITECTURE x64)
set(VCPKG_CRT_LINKAGE static)
set(VCPKG_LIBRARY_LINKAGE static)
set(VCPKG_PLATFORM_TOOLSET v142)

# Windows Server 2012 R2 / Windows 8.1 API floor for every dependency we build.
set(VCPKG_C_FLAGS "-D_WIN32_WINNT=0x0603 -DWINVER=0x0603")
set(VCPKG_CXX_FLAGS "-D_WIN32_WINNT=0x0603 -DWINVER=0x0603")
