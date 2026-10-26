@echo off
rem Publica o Wallpaper Manager como um unico .exe e cria o atalho na area de trabalho.
rem Rode de novo sempre que quiser atualizar a versao instalada.
setlocal
set "DEST=%LOCALAPPDATA%\Programs\WallpaperManager"
set "EXE=%DEST%\WallpaperManager.exe"

rem Fecha o app se estiver aberto, senao o .exe fica bloqueado e nao pode ser substituido
taskkill /im WallpaperManager.exe /f >nul 2>&1

echo Publicando em "%DEST%" ...
dotnet publish "%~dp0src\WallpaperManager\WallpaperManager.csproj" -nologo -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o "%DEST%"
if errorlevel 1 goto :erro

echo Criando atalho na area de trabalho ...
powershell -NoProfile -Command "$lnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Wallpaper Manager.lnk'; $s = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk); $s.TargetPath = '%EXE%'; $s.WorkingDirectory = '%DEST%'; $s.IconLocation = '%EXE%,0'; $s.Description = 'Gerencia papeis de parede por monitor'; $s.Save(); Write-Host ('Atalho: ' + $lnk)"
if errorlevel 1 goto :erro

echo.
echo Pronto! Abra pelo atalho "Wallpaper Manager" na area de trabalho.
exit /b 0

:erro
echo.
echo Falha na instalacao. Veja a mensagem de erro acima.
exit /b 1
