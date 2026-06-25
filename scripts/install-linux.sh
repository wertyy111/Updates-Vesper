#!/bin/bash
# Установщик Vesper Launcher для Linux (Ubuntu/Xubuntu/Debian)
# Скрипт установит необходимые зависимости для AppImage и Photino,
# сделает лаунчер исполняемым и создаст ярлык в меню приложений.

set -e

echo "==========================================="
echo "   Установка Vesper Launcher (Linux)       "
echo "==========================================="
echo ""

APPIMAGE_PATH=$1

if [ -z "$APPIMAGE_PATH" ]; then
    echo "Пожалуйста, укажите путь к скачанному файлу .AppImage."
    echo "Использование: bash install-linux.sh /путь/к/VesperLauncher-linux-x64.AppImage"
    exit 1
fi

if [ ! -f "$APPIMAGE_PATH" ]; then
    echo "Ошибка: Файл '$APPIMAGE_PATH' не найден!"
    exit 1
fi

echo "1. Установка необходимых зависимостей (требуются права root)..."
sudo apt-get update
# libfuse2 необходим для запуска AppImage на Ubuntu 22.04+ (в т.ч. Xubuntu)
# libwebkit2gtk-4.0-37 или 4.1 необходим для работы интерфейса Photino
sudo apt-get install -y libfuse2 libwebkit2gtk-4.0-37 || sudo apt-get install -y libfuse2 libwebkit2gtk-4.1-0

echo "2. Настройка прав доступа..."
chmod +x "$APPIMAGE_PATH"

echo "3. Установка лаунчера в систему..."
TARGET_DIR="$HOME/.local/bin"
mkdir -p "$TARGET_DIR"

cp "$APPIMAGE_PATH" "$TARGET_DIR/VesperLauncher.AppImage"

echo "4. Создание ярлыка..."
DESKTOP_DIR="$HOME/.local/share/applications"
mkdir -p "$DESKTOP_DIR"

cat > "$DESKTOP_DIR/vesper-launcher.desktop" << EOL
[Desktop Entry]
Name=Vesper Launcher
Comment=Minecraft Java Launcher
Exec="$TARGET_DIR/VesperLauncher.AppImage"
Icon=utilities-terminal
Terminal=false
Type=Application
Categories=Game;
EOL

chmod +x "$DESKTOP_DIR/vesper-launcher.desktop"

echo ""
echo "==========================================="
echo "Установка успешно завершена!"
echo "Теперь вы можете запустить Vesper Launcher из меню приложений."
echo "Или командой: $TARGET_DIR/VesperLauncher.AppImage"
echo "==========================================="
