param([Parameter(Mandatory=$true)][string]$Category)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$rows = New-Object 'System.Collections.Generic.List[object]'
$warnings = New-Object 'System.Collections.Generic.List[string]'
function Row($device, $property, $value, [bool]$private = $false) {
    if ($null -eq $value -or "$value".Trim() -eq '') { $value = '—' }
    if ($value -is [array]) { $value = $value -join ', ' }
    $rows.Add([pscustomobject]@{ Device = [string]$device; Property = [string]$property; Value = [string]$value; Sensitive = $private })
}
function Query($class, $namespace = 'root/cimv2', $filter = '', $timeoutSeconds = 4) {
    try {
        $options = @{ ClassName = $class; Namespace = $namespace; OperationTimeoutSec = $timeoutSeconds; ErrorAction = 'Stop' }
        if ($filter) { $options.Filter = $filter }
        @(Get-CimInstance @options)
    } catch { $warnings.Add("${class}: данные недоступны ($($_.Exception.GetType().Name)).") }
}
function FormatBytes($value) {
    if ($null -eq $value -or [double]$value -le 0) { return '—' }
    $n = [double]$value; $unit = 'Б'
    foreach ($next in @('КиБ','МиБ','ГиБ','ТиБ')) { if ($n -lt 1024) { break }; $n /= 1024; $unit = $next }
    '{0:0.##} {1}' -f $n, $unit
}
function FormatDate($value) { if ($null -ne $value) { try { return ([datetime]$value).ToString('dd.MM.yyyy HH:mm') } catch {} }; '—' }
function FormatEnum($map, $value) { if ($null -eq $value) { return '—' }; $key = [int]$value; if ($map.ContainsKey($key)) { return $map[$key] }; "Код $key" }
function Props($device, $object, $mapping, [string[]]$private = @()) {
    foreach ($key in $mapping.Keys) { Row $device $mapping[$key] $object.$key ($private -contains $key) }
}

try { switch ($Category) {
    'summary' {
        foreach ($c in (Query 'Win32_ComputerSystem')) {
            Row 'Компьютер' 'Производитель и модель' "$($c.Manufacturer) $($c.Model)"
            Row 'Компьютер' 'Имя ПК' $c.Name $true
            Row 'Память' 'Доступно системе' (FormatBytes $c.TotalPhysicalMemory)
        }
        foreach ($c in (Query 'Win32_Processor')) { Row 'Процессор' 'Модель' $c.Name; Row 'Процессор' 'Ядра / потоки' "$($c.NumberOfCores) / $($c.NumberOfLogicalProcessors)" }
        foreach ($c in (Query 'Win32_VideoController')) { Row 'Видеокарта' 'Модель' $c.Name }
        foreach ($c in (Query 'Win32_OperatingSystem')) { Row 'Windows' 'Система' "$($c.Caption), $($c.OSArchitecture)"; Row 'Windows' 'Сборка' $c.BuildNumber; Row 'Windows' 'Последняя загрузка' (FormatDate $c.LastBootUpTime) }
    }
    'cpu' {
        foreach ($c in (Query 'Win32_Processor')) {
            $d = "$($c.DeviceID) · $($c.Name)"
            Props $d $c ([ordered]@{ Manufacturer='Производитель'; SocketDesignation='Сокет'; NumberOfCores='Физические ядра'; NumberOfEnabledCore='Включённые ядра'; NumberOfLogicalProcessors='Потоки'; AddressWidth='Разрядность'; Family='Семейство (SMBIOS)'; Revision='Ревизия'; VirtualizationFirmwareEnabled='Виртуализация в BIOS'; VMMonitorModeExtensions='Аппаратная виртуализация'; SecondLevelAddressTranslationExtensions='SLAT'; ProcessorId='Идентификатор процессора' }) @('ProcessorId')
            Row $d 'Максимальная частота по SMBIOS, МГц' $c.MaxClockSpeed
            Row $d 'Кэш L2' (FormatBytes ([double]$c.L2CacheSize * 1024)); Row $d 'Кэш L3' (FormatBytes ([double]$c.L3CacheSize * 1024))
        }
        $warnings.Add('Частота из SMBIOS не отражает текущий Turbo Boost. Загрузка CPU показывается отдельно в реальном времени.')
    }
    'memory' {
        $types = @{20='DDR';21='DDR2';24='DDR3';26='DDR4';27='LPDDR';28='LPDDR2';29='LPDDR3';30='LPDDR4';34='DDR5';35='LPDDR5'}
        foreach ($c in (Query 'Win32_PhysicalMemory')) {
            $d = "$($c.DeviceLocator) · $($c.BankLabel)"
            Row $d 'Объём' (FormatBytes $c.Capacity); Row $d 'Тип' (FormatEnum $types $c.SMBIOSMemoryType)
            Props $d $c ([ordered]@{Manufacturer='Производитель';PartNumber='Артикул';SerialNumber='Серийный номер';Speed='Скорость по SMBIOS';ConfiguredClockSpeed='Настроенная скорость по SMBIOS';DataWidth='Разрядность данных';TotalWidth='Полная разрядность'}) @('SerialNumber')
            if ($c.ConfiguredVoltage -gt 0) { Row $d 'Настроенное напряжение, В' ('{0:0.###}' -f ($c.ConfiguredVoltage / 1000)) }
        }
        foreach ($c in (Query 'Win32_PhysicalMemoryArray')) { Row 'Массив памяти' 'Всего слотов по SMBIOS' $c.MemoryDevices; Row 'Массив памяти' 'Максимум по SMBIOS' (FormatBytes ([double]$c.MaxCapacityEx * 1024)) }
    }
    'board' {
        foreach ($c in (Query 'Win32_BaseBoard')) { Props 'Материнская плата' $c ([ordered]@{Manufacturer='Производитель';Product='Модель';Version='Ревизия';SerialNumber='Серийный номер'}) @('SerialNumber') }
        foreach ($c in (Query 'Win32_BIOS')) { Props 'BIOS / UEFI' $c ([ordered]@{Manufacturer='Производитель';SMBIOSBIOSVersion='Версия';SMBIOSMajorVersion='SMBIOS major';SMBIOSMinorVersion='SMBIOS minor';SerialNumber='Серийный номер'}) @('SerialNumber'); Row 'BIOS / UEFI' 'Дата выпуска' (FormatDate $c.ReleaseDate) }
        foreach ($c in (Query 'Win32_ComputerSystemProduct')) { Props 'Система' $c ([ordered]@{Vendor='Производитель';Name='Модель';Version='Версия';UUID='UUID';IdentifyingNumber='Серийный номер'}) @('UUID','IdentifyingNumber') }
        try { Row 'Безопасность' 'Secure Boot' (Confirm-SecureBootUEFI -ErrorAction Stop) } catch { Row 'Безопасность' 'Secure Boot' 'Недоступно или система работает в Legacy BIOS' }
        foreach ($c in (Query 'Win32_Tpm' 'root/cimv2/Security/MicrosoftTpm')) { Row 'TPM' 'Версия спецификации' $c.SpecVersion; Row 'TPM' 'Активирован' $c.IsActivated_InitialValue; Row 'TPM' 'Включён' $c.IsEnabled_InitialValue }
    }
    'gpu' {
        foreach ($c in (Query 'Win32_VideoController')) {
            $d = $c.Name
            Props $d $c ([ordered]@{AdapterCompatibility='Производитель';VideoProcessor='Графический процессор';DriverVersion='Версия драйвера';VideoModeDescription='Текущий режим';CurrentRefreshRate='Текущая частота, Гц';PNPDeviceID='Идентификатор устройства';Status='Состояние'}) @('PNPDeviceID')
            Row $d 'Дата драйвера' (FormatDate $c.DriverDate)
            $vram = $null
            try {
                $driver = (Get-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Enum\$($c.PNPDeviceID)" -Name Driver -ErrorAction Stop).Driver
                $vram = (Get-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Control\Class\$driver" -Name 'HardwareInformation.qwMemorySize' -ErrorAction Stop).'HardwareInformation.qwMemorySize'
            } catch {}
            Row $d 'Выделенная видеопамять (драйвер)' $(if ($vram -gt 0) { FormatBytes $vram } else { 'Драйвер не предоставил достоверное 64-битное значение' })
        }
        $warnings.Add('Объём не берётся из 32-битного AdapterRAM: он может быть неверным на видеокартах с 4 ГиБ и более. Частоты и температуры зависят от доступных датчиков.')
    }
    'disks' {
        foreach ($c in (Query 'Win32_DiskDrive')) {
            $d = "Диск $($c.Index) · $($c.Model)"
            Row $d 'Объём' (FormatBytes $c.Size)
            Props $d $c ([ordered]@{InterfaceType='Интерфейс WMI';MediaType='Носитель WMI';FirmwareRevision='Прошивка';SerialNumber='Серийный номер';Partitions='Разделов';BytesPerSector='Байт на сектор';Status='Состояние WMI'}) @('SerialNumber')
        }
        foreach ($c in (Query 'MSFT_PhysicalDisk' 'root/Microsoft/Windows/Storage')) {
            $d = "Storage · $($c.FriendlyName)"
            Row $d 'Тип носителя' (FormatEnum @{0='Не указан';3='HDD';4='SSD';5='SCM'} $c.MediaType)
            Row $d 'Шина' (FormatEnum @{0='Неизвестна';1='SCSI';3='ATA';7='USB';8='RAID';11='SATA';14='Виртуальный';15='Файловый виртуальный';16='Storage Spaces';17='NVMe';18='SCM';19='UFS'} $c.BusType)
            Row $d 'Оценка состояния Windows' (FormatEnum @{0='Исправен';1='Предупреждение';2='Неисправен';5='Неизвестно'} $c.HealthStatus)
        }
        foreach ($c in (Query 'Win32_LogicalDisk' 'root/cimv2' 'DriveType=3')) {
            $d = "Том $($c.DeviceID)"; Row $d 'Метка тома' $c.VolumeName $true
            Row $d 'Файловая система' $c.FileSystem; Row $d 'Размер' (FormatBytes $c.Size); Row $d 'Свободно' (FormatBytes $c.FreeSpace)
        }
        $warnings.Add('Оценка состояния Windows не заменяет полный разбор SMART и не гарантирует отсутствие ошибок диска.')
    }
    'display' {
        function Decode($codes) { if ($codes) { return -join @($codes | Where-Object { $_ -ne 0 } | ForEach-Object { [char]$_ }) }; '—' }
        foreach ($c in (Query 'WmiMonitorID' 'root/wmi')) {
            $d = Decode $c.UserFriendlyName
            Row $d 'Производитель' (Decode $c.ManufacturerName); Row $d 'Код продукта' (Decode $c.ProductCodeID)
            Row $d 'Серийный номер' (Decode $c.SerialNumberID) $true; Row $d 'Год / неделя выпуска' "$($c.YearOfManufacture) / $($c.WeekOfManufacture)"; Row $d 'Активен' $c.Active
        }
        foreach ($c in (Query 'WmiMonitorBasicDisplayParams' 'root/wmi')) { Row 'Размер панели' 'Ширина / высота, см' "$($c.MaxHorizontalImageSize) / $($c.MaxVerticalImageSize)" }
        Add-Type -AssemblyName System.Windows.Forms
        foreach ($c in [System.Windows.Forms.Screen]::AllScreens) { Row $c.DeviceName 'Разрешение рабочего стола, пиксели' "$($c.Bounds.Width) × $($c.Bounds.Height)"; Row $c.DeviceName 'Основной монитор' $c.Primary }
    }
    'audio' {
        foreach ($c in (Query 'Win32_SoundDevice')) { Props $c.Name $c ([ordered]@{Manufacturer='Производитель';Status='Состояние';PNPDeviceID='Идентификатор устройства'}) @('PNPDeviceID') }
    }
    'network' {
        foreach ($c in (Query 'Win32_NetworkAdapterConfiguration' 'root/cimv2' 'IPEnabled=True')) {
            $d = $c.Description
            Props $d $c ([ordered]@{MACAddress='MAC';IPAddress='IP-адреса';IPSubnet='Маски / префиксы';DefaultIPGateway='Шлюз';DNSServerSearchOrder='DNS';DHCPEnabled='DHCP';DHCPServer='DHCP-сервер'}) @('MACAddress','IPAddress','IPSubnet','DefaultIPGateway','DNSServerSearchOrder','DHCPServer')
        }
        foreach ($c in (Query 'Win32_NetworkAdapter' 'root/cimv2' 'PhysicalAdapter=True')) { Row $c.Name 'Скорость соединения, Мбит/с' $(if ($c.Speed -gt 0) { '{0:0.##}' -f ($c.Speed / 1000000) } else { '—' }); Row $c.Name 'Состояние' $c.NetConnectionStatus }
    }
    'devices' {
        foreach ($c in (Query 'Win32_PnPEntity')) {
            $d = $c.Name; if (-not $d) { $d = 'Устройство без названия' }
            Props $d $c ([ordered]@{PNPClass='Класс устройства';Manufacturer='Производитель устройства';Status='Состояние устройства';ConfigManagerErrorCode='Код диспетчера устройств'})
        }
        foreach ($c in (Query 'Win32_PnPSignedDriver' 'root/cimv2' '' 15)) {
            $d = $c.DeviceName; if (-not $d) { $d = 'Устройство без названия' }
            Props $d $c ([ordered]@{DeviceClass='Класс';Manufacturer='Производитель';DriverProviderName='Поставщик драйвера';DriverVersion='Версия';InfName='INF';IsSigned='Подписан'})
        }
        foreach ($c in (Query 'Win32_PnPEntity' 'root/cimv2' 'ConfigManagerErrorCode <> 0')) { Row $c.Name 'Код ошибки диспетчера устройств' $c.ConfigManagerErrorCode }
    }
    'windows' {
        foreach ($c in (Query 'Win32_OperatingSystem')) {
            Props 'Windows' $c ([ordered]@{Caption='Выпуск';Version='Версия ядра';BuildNumber='Сборка';OSArchitecture='Архитектура';SystemDirectory='Системный каталог';Locale='Язык (LCID)';MUILanguages='Языки интерфейса';NumberOfProcesses='Процессов'})
            Row 'Windows' 'Установлена' (FormatDate $c.InstallDate); Row 'Windows' 'Последняя загрузка' (FormatDate $c.LastBootUpTime)
        }
        $c = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
        Row 'Windows' 'Версия выпуска' $c.DisplayVersion; Row 'Windows' 'Полная сборка' "$($c.CurrentBuild).$($c.UBR)"
        $boot = @(Query 'Win32_OperatingSystem') | Select-Object -First 1
        if ($boot.LastBootUpTime) { Row 'Windows' 'Время работы' (([datetime]::Now - $boot.LastBootUpTime).ToString('d\.hh\:mm\:ss')) }
        foreach ($c in (Query 'Win32_QuickFixEngineering')) { Row $c.HotFixID $c.Description (FormatDate $c.InstalledOn) }
        $warnings.Add('Список KB содержит обновления, доступные через Windows QFE, и может быть неполным.')
    }
    'software' {
        $seen = @{}
        foreach ($path in @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*','HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*')) {
            foreach ($c in @(Get-ItemProperty $path -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -and $_.SystemComponent -ne 1 })) {
                $key = "$($c.DisplayName)|$($c.DisplayVersion)|$($c.Publisher)"; if ($seen.ContainsKey($key)) { continue }; $seen[$key] = $true
                Row $c.DisplayName 'Версия / издатель' "$($c.DisplayVersion) · $($c.Publisher)"
            }
        }
        $warnings.Add('Показаны программы из списков удаления Windows для компьютера и текущего пользователя. Portable и некоторые Microsoft Store приложения здесь отсутствуют.')
    }
    'processes' {
        foreach ($c in (Query 'Win32_Process')) { $d = "$($c.Name) · PID $($c.ProcessId)"; Row $d 'Рабочий набор RAM (снимок)' (FormatBytes $c.WorkingSetSize); Row $d 'Потоков' $c.ThreadCount; Row $d 'Дескрипторов' $c.HandleCount; Row $d 'Путь' $c.ExecutablePath $true }
        $warnings.Add('Снимок на момент сбора; процессы могут уже завершиться. Командные строки не собираются.')
    }
    'services' {
        foreach ($c in (Query 'Win32_Service')) { $d = "$($c.DisplayName) · $($c.Name)"; Row $d 'Состояние / запуск' "$($c.State) / $($c.StartMode)"; Row $d 'PID' $c.ProcessId }
    }
    'startup' {
        foreach ($c in (Query 'Win32_StartupCommand')) { Row $c.Name 'Расположение' $c.Location; Row $c.Name 'Команда' $c.Command $true }
        $warnings.Add('Снимок Win32_StartupCommand. Задачи планировщика и все механизмы автозапуска в этот список не входят.')
    }
    'sensors' {
        $labels = @{Voltage='Напряжение, В';Current='Ток, А';Power='Мощность, Вт';Clock='Частота, МГц';Temperature='Температура, °C';Load='Загрузка, %';Frequency='Частота, Гц';Fan='Вентилятор, об/мин';Flow='Поток, л/ч';Control='Управление, %';Level='Уровень, %'}
        foreach ($namespace in @('root/LibreHardwareMonitor','root/OpenHardwareMonitor')) {
            foreach ($c in (Query 'Sensor' $namespace)) {
                $label = [string]$c.SensorType; if ($labels.ContainsKey($label)) { $label = $labels[$label] }
                $value = $null; if ($null -ne $c.Value) { $value = '{0:0.###}' -f $c.Value }
                Row "$($c.Parent) · $($c.Name)" $label $value
            }
        }
        foreach ($c in (Query 'MSAcpi_ThermalZoneTemperature' 'root/wmi')) {
            if ($c.CurrentTemperature -gt 0) { $t = $c.CurrentTemperature / 10 - 273.15; if ($t -gt -30 -and $t -lt 150) { Row "ACPI · $($c.InstanceName)" 'Температура зоны, °C (не CPU)' ('{0:0.0}' -f $t) } }
        }
        foreach ($c in (Query 'Win32_Battery')) {
            Row $c.Name 'Заряд, %' $c.EstimatedChargeRemaining
            Row $c.Name 'Состояние питания' (FormatEnum @{1='Разряд';2='Сеть / нет разряда';3='Полностью заряжена';4='Низкий заряд';5='Критический заряд';6='Заряжается';7='Заряжается, высокий уровень';8='Заряжается, низкий уровень';9='Заряжается, критический уровень';10='Не определено';11='Частично заряжена'} $c.BatteryStatus)
            if ($c.EstimatedRunTime -gt 0 -and $c.EstimatedRunTime -lt 71582788) { Row $c.Name 'Оценка остатка, мин' $c.EstimatedRunTime }
        }
        $warnings.Add('Датчики читаются из уже работающего LibreHardwareMonitor / OpenHardwareMonitor с включённым WMI. x2zapret не устанавливает драйвер датчиков. ACPI-зона не является температурой процессора. Отсутствующие показания не подменяются.')
    }
    default { throw 'Unknown PC information category' }
}
} catch { $warnings.Add('Сбор раздела завершён частично: ' + $_.Exception.GetType().Name) }
[pscustomobject]@{rows=@($rows.ToArray());warnings=@($warnings.ToArray())} | ConvertTo-Json -Depth 5 -Compress
