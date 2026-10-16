# Vá Il2CppInterop cho Unity 6000.6 (GoPetHD)

MelonLoader 0.7.3 dùng Il2CppInterop 1.5.1-ci.845. Bản này không biết layout `Il2CppClass` của Unity 6000.6, nên gặp lỗi
`Base class Il2CppSystem.Object is value type and can't be inherited from` / `No Support Module Loaded!`.

Layout của Unity 6000.6 (đo từ các hàm export trong GameAssembly.dll):
- Bỏ trường `implementedInterfaces`. Mảng `interfaceOffsets` (0x90) dùng chung cho danh sách interface; mỗi phần tử 16 byte, trong đó +0xC là cờ "implemented".
- `events`, `properties`, `nestedTypes` chuyển xuống sau `typeHierarchy`. `methods` nằm ở 0x88.
- Từ `instance_size` (0xF0) trở đi, các trường giữ nguyên thứ tự như Class_29_2 nhưng lệch -8 byte. Vtable bắt đầu ở 0x130.

## Build lại
```
git clone https://github.com/BepInEx/Il2CppInterop.git
cd Il2CppInterop
git checkout f03c8f4ae507d47ea814f3d11d1ec6b0391c1576
git apply ../unity6000.6-class-layout.patch
dotnet build Il2CppInterop.Runtime/Il2CppInterop.Runtime.csproj -c Release -p:Version=1.5.1-ci.845 -p:GeneratePackageOnBuild=false
```
Sau đó copy `bin/Il2CppInterop.Runtime/net6.0/Il2CppInterop.Runtime.dll` vào `goPetHD_mod/MelonLoader/net6/`.
Bản gốc được giữ ở `goPetHD_mod/MelonLoader/net6/Il2CppInterop.Runtime.dll.orig`.
Nếu cập nhật MelonLoader, file vá có thể bị ghi đè; khi đó hãy copy lại.
