# LayoutSeek 0.3.4 preview

LayoutSeek 0.3.4 is a portable preview for Windows 11 24H2, x64. The interface is in Russian.

This release adds optional startup for the portable app. The checkbox creates a shortcut in the current user's Windows Startup folder. Startup mode opens in the tray and enables correction after the app list loads. Windows can separately disable the shortcut in Startup settings.

Fixed ignored startup-checkbox clicks when returning to the app window.

The Search engine is unchanged from 0.3.3. It corrects keyboard-layout mistakes in native taskbar and Start search, using installed app names and physical-key mappings.

Extract the portable ZIP and keep its four runtime files together. Run `SearchLayout.exe`, then click «Включить». No installer or administrator privileges are required. The binaries are unsigned; SmartScreen may show a warning.

This remains a preview. Russian ↔ English US has native test coverage; other layout pairs have synthetic coverage only. Actual Windows sign-in is untested, and an early missing-first-character case remains unexplained. See the [README](https://github.com/alekzmasai/LayoutSeek#readme) for limits, startup removal and build instructions.

## По-русски

LayoutSeek 0.3.4 является переносной предварительной версией для Windows 11 24H2, x64. Интерфейс на русском.

В этом выпуске добавлен добровольный автозапуск переносной программы. Флажок создаёт ярлык в папке автозагрузки текущего пользователя Windows. Режим автозапуска открывает программу в трее и включает исправления после загрузки каталога. Windows может отдельно отключить ярлык в настройках автозагрузки.

Исправлен пропуск нажатия на флажок автозапуска при возвращении к окну.

Движок Search не менялся после 0.3.3. Он исправляет ошибки раскладки в штатном поиске панели задач и Пуска, используя названия установленных программ и соответствия физических клавиш.

Распакуйте переносной ZIP и оставьте рядом четыре файла программы. Откройте `SearchLayout.exe` и нажмите «Включить». Установщик и права администратора не нужны. Файлы не подписаны; SmartScreen может показать предупреждение.

Это предварительный выпуск. RU ↔ English US проверена в настоящем поиске; другие пары только на искусственных данных. Настоящий вход в Windows ещё не проверен, а ранний случай пропажи первого символа остаётся неразобранным. Ограничения, отключение автозапуска и сборка описаны в [README](https://github.com/alekzmasai/LayoutSeek/blob/main/README.ru.md).
