# FingReader.WindowsHello 1.0.0 — подключение к своим приложениям

Библиотека подтверждает текущего пользователя Windows через штатное окно Windows Hello. Используется настроенный в Windows отпечаток, лицо или PIN. Проверка не зависит от базы FingReader и не требует DLL Futronic в приложении. Внутри сохранена проверенная на FS80H передача фокуса системному окну.

## Требования

- Windows 11, сборка 22000 или новее, интерактивный сеанс пользователя.
- **Управляемая DLL для C#/.NET 8 и новее**: WinForms, WPF, WinUI или другой настольный интерфейс с HWND. Подходит x64 и x86; сборка библиотеки AnyCPU. Это не нативная DLL с экспортами для C/C++ и не .NET Framework 4.x.
- В проекте приложения целевая платформа должна быть, например, `net8.0-windows10.0.22000.0` или `net10.0-windows10.0.22000.0`. Номер платформы не ниже 22000.
- Пользователь должен настроить Windows Hello/PIN в «Параметры → Учётные записи → Варианты входа». Для FS80H нужен работающий WBF-драйвер; регистрация выполняется средствами Windows.

## Состав готового комплекта

- `lib/FingReader.WindowsHello.dll` — наша библиотека; рядом XML-подсказки и PDB.
- `lib/Microsoft.Windows.SDK.NET.dll` и `lib/WinRT.Runtime.dll` — зависимости Microsoft для Windows Runtime. Сохраняйте комплект вместе; Windows-проект .NET также получает эти зависимости из своего SDK.
- `nuget/FingReader.WindowsHello.1.0.0.nupkg` — локальный пакет для удобного подключения; на публичные серверы он не публиковался.
- `examples/HelloUnlock` — полный пример WinForms с автоматическим запросом, блокировкой, тайм-аутом и защитой от устаревшего ответа. В комплекте он ссылается **на готовую DLL**, а не на проект библиотеки.
- `source` — исходники библиотеки, тестов и документации.
- `THIRD-PARTY.md` — сведения о зависимостях Microsoft.

Ни отпечатков, ни псевдонимов, ни личных настроек в комплекте нет. DLL не ведёт журнал и не пишет файлы.

## Подключение

В Visual Studio: «Зависимости → Добавить ссылку на проект/сборку → Обзор», выберите `lib/FingReader.WindowsHello.dll`. В `.csproj` это выглядит так (путь относительно вашего проекта):

```xml
<PropertyGroup>
  <TargetFramework>net8.0-windows10.0.22000.0</TargetFramework>
</PropertyGroup>
<ItemGroup>
  <Reference Include="FingReader.WindowsHello">
    <HintPath>lib/FingReader.WindowsHello.dll</HintPath>
    <Private>true</Private>
  </Reference>
</ItemGroup>
```

Добавьте `using FingReader.WindowsHello;`. Сам вызов:

```csharp
HelloResult result = await HelloAuthentication.VerifyAsync(
    Handle, "Подтвердите вход в наше приложение.");

if (result.IsVerified)
    OpenProtectedSection();
else
    KeepSectionLocked();
```

`Handle` — дескриптор видимого активного **главного окна вашего процесса**, а не дочернего контрола. WinForms: `this.Handle` после `Shown`. WPF: `new System.Windows.Interop.WindowInteropHelper(this).Handle` после показа окна. WinUI: `WinRT.Interop.WindowNative.GetWindowHandle(this)`. Не используйте `IntPtr.Zero`, HWND другого приложения или вызов из службы.

Не блокируйте интерфейс через `.Wait()`/`.Result`: используйте `await`. DLL выполняет WinRT-запрос в рабочем MTA-потоке, а приложение продолжает обрабатывать сообщения окна. DLL сама не активирует фоновое приложение: для автоматического запроса вызывайте её после показа/активации своей формы.

### Подключение исходников

При работе в репозитории вместо бинарной ссылки можно добавить:

```xml
<ProjectReference Include="..\src\FingReader.WindowsHello\FingReader.WindowsHello.csproj" />
```

### Локальный NuGet

Добавьте папку `nuget` из комплекта как локальный источник пакетов. Например, из каталога комплекта (команда PowerShell — одна строка):

```powershell
dotnet add 'C:\Projects\MyApp\MyApp.csproj' package FingReader.WindowsHello --version 1.0.0 --source '.\nuget'
```

Путь к своему приложению замените. Для восстановления SDK/других пакетов может потребоваться доступ к обычному источнику nuget.org. SDK .NET устанавливается отдельно, либо распространяйте готовое приложение как self-contained.

## API

```csharp
Task<HelloAvailability> HelloAuthentication.CheckAvailabilityAsync();
Task<HelloResult> HelloAuthentication.VerifyAsync(
    nint ownerWindow,
    string message = "Подтвердите вход в приложение.",
    CancellationToken cancellationToken = default);
```

`CheckAvailabilityAsync` не открывает окно. `Available` означает только, что проверка настроена: это **не** успешная аутентификация и не разрешение доступа. Не обязательно вызывать его перед каждым запросом.

`HelloResult` содержит `Status`, `HResult` (код исключения при Error) и `IsVerified`. Разрешать доступ можно **только** при `IsVerified == true`. У результата по умолчанию доступ закрыт. Неизвестный ответ Windows также не открывает доступ.

| Status | Значение для приложения |
| --- | --- |
| Verified | Windows подтвердила текущего пользователя |
| Canceled | Отмена пользователем или токеном; оставить закрытым |
| DeviceNotPresent | Нет доступного средства подтверждения |
| NotConfiguredForUser | Нужно настроить Hello/PIN в Windows |
| DisabledByPolicy | Проверка запрещена политикой, не обходить |
| DeviceBusy | Другой запрос уже выполняется |
| RetriesExhausted | Windows исчерпала допустимые попытки |
| InvalidOwnerWindow | Неверный HWND, чужое/дочернее/скрытое/свёрнутое окно |
| OwnerNotActive | Сначала вернуть пользователя в своё окно |
| UnsupportedSystem | Нужна поддерживаемая Windows |
| Error | Доступ закрыт; при диагностике использовать HResult |

Текст сообщения не пустой, до 512 символов. Ошибочные аргументы сообщения вызывают ArgumentException/ArgumentOutOfRangeException. Рабочие ошибки Windows возвращаются как HelloResult, исключения не превращаются в успех.

## Жизненный цикл и отмена

DLL выполняет разовую проверку и не хранит состояние «приложение открыто». Хозяин приложения должен закрывать защищённый раздел при завершении/блокировке Windows, по своему тайм-ауту и при смене пользователя. В полном примере это уже реализовано.

Для закрытия формы передайте CancellationToken и отмените его при закрытии. Отмена нативного окна — best effort: Windows может завершить запрос не мгновенно. После отмены DLL не выдаёт успешного результата, даже если одновременно пришёл Verified. Внутри одного процесса запросы сериализованы: второй получает DeviceBusy. Отмена или ошибка не оставляют блокировку запросов навсегда.

Защищайтесь от устаревшего ответа: увеличивайте счётчик поколения при каждой блокировке и проверяйте его после `await`. Схема:

```csharp
private int generation;
private bool checking;
private readonly CancellationTokenSource lifetime = new();

private async Task UnlockAsync()
{
    if (checking) return;
    checking = true;
    int attempt = ++generation;
    KeepSectionLocked();
    try
    {
        var result = await HelloAuthentication.VerifyAsync(
            Handle, "Подтвердите вход.", lifetime.Token);
        if (!IsDisposed && attempt == generation && result.IsVerified)
            OpenProtectedSection();
    }
    finally { checking = false; }
}

private void LockApplication()
{
    ++generation; // Ответ прежнего запроса больше не принимается.
    KeepSectionLocked();
}
```

Это фрагмент WinForms, методы OpenProtectedSection/KeepSectionLocked реализуются в вашем приложении. Отмените `lifetime` при закрытии. Готовая форма в `examples/HelloUnlock` показывает полный код, включая обработку сеанса Windows. Не открывайте раздел просто потому, что окно Hello закрылось или устройство нашлось.

## Фокус окна Hello

Первые пять секунд DLL ищет ровно одно **новое** видимое окно системного CredentialUIBroker.exe в текущем сеансе, сверяет полный системный путь и передаёт разрешение на фокус только этому PID. Ограничение — до трёх попыток. Если пользователь переключился в другое приложение, фокус не отбирается. Нет TOPMOST, поиска по локализованному заголовку, AttachThreadInput и глобального ASFW_ANY.

Это ограниченная эвристика для показа системного интерфейса, не часть решения о доступе. Если брокер переиспользует старое окно или одновременно появляются несколько подходящих окон, DLL не выбирает наугад. Решение о подтверждении всегда берётся только из WinRT API.

## Что эта DLL не делает

- Не различает людей по псевдонимам FingReader, не возвращает имя/номер пальца и не выбирает исключительно FS80H.
- Не гарантирует, что использован именно отпечаток: Windows может разрешить PIN/другой настроенный способ.
- Не предоставляет серверные роли, токен входа в ваш сервер, шифрование файлов или защиту от изменения кода приложения. Это подтверждение присутствия текущего пользователя Windows. Для реальных секретов отдельно нужны безопасное хранение и проверка прав.
- Не меняет драйверы, PIN, UAC и политику Windows; не получает изображение отпечатка или сам PIN.

## Сборка и проверка из репозитория

Каждая команда PowerShell — одна строка:

```powershell
dotnet build .\src\FingReader.WindowsHello\FingReader.WindowsHello.csproj -c Release
```

```powershell
dotnet pack .\src\FingReader.WindowsHello\FingReader.WindowsHello.csproj -c Release -o .\work\windowshello-dist\nuget
```

```powershell
dotnet run --project .\tests\WindowsHello.Tests\WindowsHello.Tests.csproj -c Release
```

```powershell
dotnet publish .\samples\HelloUnlock\HelloUnlock.csproj -c Release -r win-x64 --self-contained true -o .\samples\HelloUnlock\publish
```

В готовом комплекте пример собирается из его папки командой `dotnet publish .\HelloUnlock.csproj -c Release -r win-x64 --self-contained true -o .\publish`. Затем запускается `Запустить.cmd`.

## Официальная документация

- [UserConsentVerifier для настольных приложений](https://learn.microsoft.com/en-us/uwp/api/windows.security.credentials.ui.userconsentverifier)
- [RequestVerificationForWindowAsync, HWND активного окна, минимальная сборка 22000](https://learn.microsoft.com/en-us/windows/win32/api/userconsentverifierinterop/nf-userconsentverifierinterop-iuserconsentverifierinterop-requestverificationforwindowasync)
- [AllowSetForegroundWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-allowsetforegroundwindow)
