# escape=`

FROM mcr.microsoft.com/dotnet/framework/sdk:3.5-windowsservercore-ltsc2019 as Build

# Set cmd as default shell
SHELL ["cmd", "/S", "/C"]

# Setup vs_buildtools.exe
ADD https://aka.ms/vs/16/release/channel C:\TEMP\VisualStudio.chman
ADD https://aka.ms/vs/16/release/vs_buildtools.exe C:\TEMP\vs_buildtools.exe

# VS2019 C++ stuff
RUN C:\TEMP\vs_buildtools.exe --quiet --wait --norestart --nocache `
    --installPath C:\BuildTools `
    --channelUri C:\TEMP\VisualStudio.chman `
    --installChannelUri C:\TEMP\VisualStudio.chman `
    --add Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    || if "%ERRORLEVEL%"=="3010" exit /b 0

# 10 SDK
RUN C:\TEMP\vs_buildtools.exe --quiet --wait --norestart --nocache `
    --installPath C:\BuildTools `
    --channelUri C:\TEMP\VisualStudio.chman `
    --installChannelUri C:\TEMP\VisualStudio.chman `
    --add Microsoft.VisualStudio.Component.Windows10SDK.19041 `
    || if "%ERRORLEVEL%"=="3010" exit /b 0

# NetCore SDK
RUN C:\TEMP\vs_buildtools.exe --quiet --wait --norestart --nocache `
    --installPath C:\BuildTools `
    --channelUri C:\TEMP\VisualStudio.chman `
    --installChannelUri C:\TEMP\VisualStudio.chman `
    --add Microsoft.NetCore.Component.SDK `
    || if "%ERRORLEVEL%"=="3010" exit /b 0

# Add the source code
ADD . C:/source
WORKDIR C:/source

# Restore nuget packages
RUN msbuild Confuser2.sln /t:Restore /p:Configuration=Release

# Set VS environment and compile
RUN call C:\BuildTools\VC\Auxiliary\Build\vcvarsall.bat amd64 && `
 msbuild /maxCpuCount Confuser2.sln /p:Configuration=Release /verbosity:minimal

# Create new image
FROM mcr.microsoft.com/dotnet/framework/runtime:3.5-windowsservercore-ltsc2019 as ConfuserEx2
COPY --from=Build C:/source/Confuser.CLI/bin/Release/net461 C:/ConfuserEx2

ENTRYPOINT [ "C:/ConfuserEx2/Confuser.CLI.exe" ]
