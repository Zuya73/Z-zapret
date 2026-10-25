# Файлы zapret и Flowseal

В `bin/` находятся готовые файлы модуля обхода zapret, сетевого драйвера WinDivert, среды Cygwin и служебных пакетов. Они перенесены из комплекта Flowseal 1.10.4 без изменения содержимого. В `licenses/` сохранены их лицензии.

- [Исходный комплект Flowseal](https://github.com/Flowseal/zapret-discord-youtube)
- [Исходники zapret](https://github.com/bol-van/zapret)
- [Комплект zapret для Windows](https://github.com/bol-van/zapret-win-bundle)
- [Источники и лицензии в проекте](../../docs/THIRD-PARTY-NOTICES.txt)

Наш код находится в `src`, стандартные списки — в `resources/lists`. При сборке готовые файлы копируются в `bin` рабочего пакета. Их контрольные суммы записываются в отдельный список и проверяются перед использованием.

Точная версия, исходный коммит и сведения о файлах находятся в `upstream.lock.json`. Перенос новой версии выполняет `tools/sync_flowseal.py`. Порядок описан в [инструкции по обновлению Flowseal](../../docs/UPSTREAM.md).
