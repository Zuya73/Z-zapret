# Сторонние компоненты zapret

`bin/` содержит неизменённые файлы движка, WinDivert, Cygwin и фейковые пакеты,
импортированные из исходной сборки Flowseal 1.10.4.
`licenses/` содержит сопутствующие лицензии.

- Исходный проект: https://github.com/Flowseal/zapret-discord-youtube
- Движок: https://github.com/bol-van/zapret
- Windows-комплект: https://github.com/bol-van/zapret-win-bundle
- Сведения о лицензиях и источниках: [THIRD-PARTY-NOTICES](../../docs/THIRD-PARTY-NOTICES.txt).

Код приложения находится в `src`, начальные списки - в `resources/lists`.
При сборке файлы копируются в `bin` пакета и включаются в манифест SHA256.
Перенос каталогов в репозитории не изменяет бинарные файлы.
