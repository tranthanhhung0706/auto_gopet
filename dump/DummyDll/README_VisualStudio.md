# GoPetHD – mở bằng Visual Studio

Các DLL trong thư mục này là **DummyDll của Il2CppDumper**: chỉ có khai báo class/method, thân hàm rỗng.
Không thể build lại game từ chúng. Để viết code chạy trong game, dùng project mod MelonLoader `GoPetMod`.

## Mở và chạy
1. Mở `GoPetHD.sln` bằng Visual Studio 2022 trở lên (cần workload ".NET desktop development").
2. Build (Ctrl+Shift+B): `GoPetMod.dll` được tự copy vào `goPetHD_mod\Mods`.
3. F5 (profile "GoPet (MelonLoader)") sẽ chạy `goPetHD_mod\GoPet.Unity.exe` cùng mod.
   Log của mod nằm trong cửa sổ console của MelonLoader và trong `goPetHD_mod\MelonLoader\Latest.log`.

## Tham chiếu
- `goPetHD_mod\MelonLoader\net6` – MelonLoader, Harmony, Il2CppInterop.
- `goPetHD_mod\MelonLoader\Il2CppAssemblies` – assembly interop của game (`Il2CppGoPet.Client`, `Il2CppGoPet.Core`, UnityEngine.*).
  Thư mục này do MelonLoader sinh ra mỗi khi game cập nhật; hãy chạy game một lần nếu thư mục bị thiếu.
- Nếu di chuyển thư mục game, build với `dotnet build -p:GameDir="đường\dẫn\mới"` hoặc sửa `GameDir` trong `GoPetMod.csproj`, rồi sửa `Properties\launchSettings.json`.

## Tra cứu code game
- `..\dump.cs` / `..\gopet_only.cs`: danh sách class, field, method kèm địa chỉ RVA.
- Trong VS: chuột phải vào kiểu `Il2Cpp...` → Go To Definition, hoặc dùng Object Browser.
- Tên class/method trong GoPet đã bị obfuscate (`c`, `ish`, `hfq`...), nên phải đối chiếu với `dump.cs`.
