@echo off
setlocal EnableExtensions DisableDelayedExpansion

pushd "%~dp0" || exit /b 1

git fetch origin || goto :done
git merge origin/main --ff-only || goto :done
dotnet build || goto :done
"artifacts\bin\Tiger\debug\tiger.exe" %*

:done
set "exitCode=%errorlevel%"
popd
exit /b %exitCode%
