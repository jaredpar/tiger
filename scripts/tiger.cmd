@echo off
setlocal

git fetch origin || exit /b 1
git merge origin/main --ff-only || exit /b 1
dotnet build || exit /b 1
artifacts\bin\Tiger\debug\tiger.exe %*
