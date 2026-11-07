# DjShrek-Draiver Cyber Club

Самостоятельная Windows-утилита под брендом DjShrek-Draiver. Проект не копирует закрытый код, графику или базу Driver Booster.

## Реализовано в первой функциональной сборке
- WPF-интерфейс Cyber Club: обзор, устройства, драйверы, диагностика.
- Инвентаризация PnP через Windows CIM: имя, класс, статус, Instance ID и код конфигурации.
- Загрузка CPU, использование RAM и версия Windows по CIM.
- Экспорт пакетов драйверов штатной командой pnputil.
- Установка выбранного INF через pnputil после подтверждения и UAC.
- Self-contained Windows x64 pipeline в GitHub Actions.

## Ограничения текущей версии
Это ранняя функциональная тестовая версия, а не полный паритет Driver Booster. Пока нет автоматического каталога обновлений и подбора новых версий драйверов, мониторинга температур CPU/GPU, игрового оптимизатора и автоматического rollback. Эти функции нельзя имитировать фиктивными данными.

## Требования
Windows 10/11 x64. Для опубликованного EXE .NET Runtime не требуется. Для разработки нужен .NET 8 SDK на Windows.

## Локальная сборка
    dotnet restore .\DjShrek-Draiver.sln
    dotnet build .\DjShrek-Draiver.sln -c Release
    dotnet publish .\src\DjShrek.Draiver.App\DjShrek.Draiver.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\artifacts\win-x64

## Безопасность
Сканирование не меняет систему. Установка требует подтверждения и UAC. Используйте только INF-пакеты из доверенных источников. Windows выполняет собственные проверки подписи и совместимости.
