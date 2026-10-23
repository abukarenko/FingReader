# Зависимости FingReader.WindowsHello

Собственная библиотека FingReader.WindowsHello использует штатные API Windows Hello. Сторонних библиотек распознавания, DLL Futronic и изображений отпечатков в этом модуле нет.

Microsoft.Windows.SDK.NET.dll и WinRT.Runtime.dll взяты без изменений из Microsoft.Windows.SDK.NET.Ref 10.0.22000.57, официального пакета Microsoft. Применяются условия Microsoft Windows SDK: https://aka.ms/WinSDKLicenseURL . Метаданные пакета: https://www.nuget.org/packages/Microsoft.Windows.SDK.NET.Ref/10.0.22000.57 .

WinRT.Runtime относится к C#/WinRT, Microsoft, MIT License: https://github.com/microsoft/CsWinRT . В готовом комплекте текст лицензии находится в licenses/CsWinRT-LICENSE.txt; условия SDK из официальной ссылки — в licenses/Windows-SDK-License.rtf.

Контрольные суммы файлов приведены в SHA256SUMS.txt. .NET Runtime в lib не включён: он устанавливается отдельно или включается при self-contained публикации приложения-хозяина. Системные user32/kernel32/WinRT DLL Windows не копируются и не заменяются.
