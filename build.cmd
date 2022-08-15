for /f "delims=" %%i in ('vswhere -all -latest -products * -prerelease -property installationPath') do set _p=%%i
if not "%_p%" == "" call "%_p%\Common7\Tools\VsMSBuildCmd.bat" %*
MSBuild /t:Restore /property:Configuration=Release ExpectExtension.sln
MSBuild /t:Build /property:Configuration=Release ExpectExtension.sln
