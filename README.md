Forked from SilkyFowl/FuncUI.VideoPlayer to bump to net6 and add as a submodule

To add as submodule I ran:
```powershell
git clone --depth=1 --filter=blob:none --no-checkout https://github.com/laquazi/LibVLCSharp.Avalonia.FuncUI LibVLCSharp.Avalonia.FuncUI
cd .\LibVLCSharp.Avalonia.FuncUI
git sparse-checkout set 'src/LibVLCSharp.Avalonia.FuncUI' '.paket'
git checkout main
cd ..
git submodule add https://github.com/laquazi/LibVLCSharp.Avalonia.FuncUI LibVLCSharp.Avalonia.FuncUI
```
optional:
```powershell
git submodule absorbgitdirs
```
