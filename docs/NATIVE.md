# Native C ABI v1 — Windows x64

## Doğrulama durumu

Bu sürümün native giriş noktaları managed test sürecinde çağrı kuralı ve pointer
aracılığıyla test edilmiştir. 12 giriş noktası testi geçmiştir; gerçek NativeAOT DLL,
C istemci, Lua 5.4 ve LuaJIT runtime doğrulaması henüz tamamlanmamıştır.
Native kaynakların bulunması, test edilmiş bir native ürün sağlandığı anlamına gelmez.
Müşteri dağıtımından önce aşağıdaki araçlarla derleyip gerçek DLL ve dil
bağlayıcılarının kabul testlerini kendi ortamınızda tamamlayın.

## Derleme

Windows x64 build host, .NET 8 SDK, Visual Studio 2022 Build Tools'un
**Desktop development with C++** araçları ve Windows SDK gerekir.
Testler ayrıca Python 3 ve CMake kullanır. [Microsoft NativeAOT önkoşulları](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/).

```powershell
.\build-native.ps1
```

Betik managed regresyon ve giriş noktası testlerini çalıştırır; NativeAOT publish
yapar; aynı ABI testlerini gerçek DLL üzerinden tekrar çalıştırır. Ardından C
örneğini derler ve Python/C dış istemci testlerini çalıştırır. Her hata betiği durdurur.
Başarılı çıktı `artifacts/native/win-x64/IntegrityGuard.Native.dll` ve `.lib` olur.
Normal `bin/Release/net8.0/...` managed DLL dosyasını native diye dağıtmayın.
Bu betik sistem araçlarını kurmaz veya paketleri yayımlamaz.

Native kitaplık self-contained olduğundan son kullanıcıda .NET kurulumu gerekmez.
NativeAOT DLL'i süreç boyunca yüklü kalmalıdır; `FreeLibrary`/`dlclose` desteklenmez.
[Microsoft native kütüphane belgesi](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/libraries).
Lua bağlayıcıları Windows'ta DLL'i pinler. Oturum kapatma DLL'i boşaltmaz.

## Sözleşme

Header: `native/include/integrity_guard.h`. Cdecl, ABI sürümü 1, sabit genişlikli
integer türleri kullanılır. Handle `uint64_t`'dir; pointer veya Lua number değildir.
Tüm string girişleri uzunluğu ayrıca verilen UTF-8 baytlardır; sonlandırıcı NUL
gerekmez. Caller pointer'ları belirtilen uzunlukta geçerli tutmalıdır; yanlış
adresleri API doğrulayamaz ve süreç çökebilir. Pointer giriş/çıkış bölgeleri
birbiriyle örtüşmemelidir. ABI güvenilmeyen süreç içi kod için izolasyon değildir.

| Çağrı | İşlev |
| --- | --- |
| `ig_abi_version()` | 1 döndürür; sürümü açılışta kontrol edin |
| `ig_open(json, length, &handle)` | Politikayı kopyalar, oturum açar; henüz dosya doğrulamaz |
| `ig_check(handle, phase)` | Tam doğrulama; başarılıysa doğrulanmış kopyaları saklar |
| `ig_copy_file(handle, name, length, buffer, capacity, &written)` | Son başarılı kontroldeki dosyanın baytlarını caller tamponuna kopyalar |
| `ig_close(handle)` | Kütüphanenin oturum ve kopya referanslarını bırakır |

`ig_open` yapılandırmasının tüm alanları zorunludur; bilinmeyen veya tekrarlanan
alanlar reddedilir. JSON en fazla 128 KiB olabilir. Kök dizin absolute olmalıdır.

```json
{
  "root": "C:\\MyApplication",
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\n...\n-----END PUBLIC KEY-----",
  "requiredFiles": ["protected.lua"],
  "minimumRelease": 2
}
```

Bu JSON sadece biçim örneğidir; gerçek public key gerekir. JSON'u production'da
uygulamanın güvenilen yapılandırmasından üretin. Kullanıcının değiştirebildiği
dosyadan public key veya minimum sürüm yükleyip güvenmeyin. Private key verilmez.
Dosya envanteri imzalı manifest ile tam eşleşmelidir.

Phase: 0 başlangıç, 1 periyodik, 2 işlem öncesi. Her kontrol önce eski kopyaları
bırakır. Başarısızlık oturumu engeller; sonraki kontrol 108 döndürür. Başarısız
kontrolden sonra `ig_copy_file` 4 döndürür, eski doğrulanmış içerik verilmez.
Oturum başına managed API'deki 32 dosya / 64 MiB toplam sınırları geçerlidir.
Süreç başına en fazla 16 native oturum vardır. Kapatılan handle yeniden kullanılmaz.

## Bellek ve eşzamanlılık

Önce `destination=NULL, capacity=0` ile boyut sorun. Dolu dosyada 3, boş dosyada
0 döner. Caller gereken tamponu ayırır ve ikinci çağrıda baytları alır. 3 dönerse
tampona hiçbir şey yazılmaz. `written` 0/3 sonuçlarında kopyalanan/gerekli boyuttur;
diğer sonuçlarda 0 olur. Dosya içindeki NUL baytları korunur; string sonlandırıcı
eklenmez. Caller kendi tamponunu kendi allocator'ıyla serbest bırakır.

`ig_close` snapshot referanslarını bırakır; GC fiziksel geri kazanım zamanını
yönetir, sıfırlama garantisi verilmez. Caller'a kopyalanmış baytlar caller'a aittir;
oturum kapatılınca geri alınamaz. Hassas tamponların yaşam süresini host yönetir.

Oturum başına tek workflow sahibi kullanın. Tek çağrılar kilitlidir fakat check,
boyut sorgusu, kopyalama ve iş işlemi birlikte atomik değildir. Bunları başka
thread'lerde aynı handle üzerinde iç içe çalıştırmayın. Ayrı oturumlar kullanılabilir.
İşlem sırasında diski yeniden okumayın; Lua `load` fonksiyonuna alınan baytları verin.

## Sonuç kodları

0 başarı; 1 geçersiz ABI argümanı; 2 geçersiz/kapalı handle; 3 küçük tampon;
4 henüz doğrulanmış kopya yok; 5 istenen isim doğrulanmış listede yok;
6 oturum sınırı; 7 beklenmeyen iç hata.

100 geçersiz politika; 101 geçersiz imza; 102 geçersiz manifest;
103 geçersiz digest; 104 eski sürüm; 105 dosya değişmiş;
106 eksik manifest girdisi; 107 dosya/kriptografi/erişim doğrulama hatası;
108 oturum engellenmiş. Hatalar exception nesnesi olarak ABI dışına çıkmaz;
bozuk pointer/stack overflow gibi ölümcül süreç hatalarına kurtarma garantisi yoktur.

## LuaJIT 2.1

`bindings/luajit` dizinini uygulamanın güvenilen `package.path` ayarına ekleyin.
```lua
local guard = require("integrity_guard")
local session, code = guard.open(native_dll_absolute_path, trusted_config_json)
assert(session, tostring(code))
local source, status = session:read("protected.lua", guard.BEFORE_OPERATION)
if not source then session:close(); error(tostring(status)) end
local chunk, err = load(source, "@verified/protected.lua", "t", {})
session:close()
assert(chunk, err)
return chunk()
```

`samples/luajit/run_verified.lua` işlem hatasında da close yapan örnektir.
Handle ffi `uint64_t` olarak tutulur. Buffers LuaJIT'e aittir. Sonlandırıcı GC
yardımı vardır; uygulama yine de `close()` çağırmalıdır. `read()` her seferinde
kontrol yapar; `check()` / `close()` boolean+kod, `read()` string+kod veya nil+kod döndürür.

## Standart Lua 5.4

`bindings/lua54` gerçek Lua C API modülüdür; LuaJIT modülüyle değiştirilemez.
Host ile aynı x64 mimaride Lua 5.4 header ve import library'si gerekir.

```powershell
cmake -S bindings/lua54 -B artifacts/lua54 -A x64 `
  -DLUA_INCLUDE_DIR=C:/Lua54/include -DLUA_LIBRARY=C:/Lua54/lib/lua54.lib `
  -DINTEGRITY_GUARD_LIBRARY=C:/project/artifacts/native/win-x64/IntegrityGuard.Native.lib
cmake --build artifacts/lua54 --config Release
```

Yollar örnektir. Oluşan `integrity_guard.dll` modülünü, `IntegrityGuard.Native.dll`
bağımlılığını ve host'un Lua runtime'ını güvenilen uygulama dizinine yerleştirin.
Host DLL arama politikasını bu güvenilen dizinle sınırlandırmalıdır. Modülü
uygulamanın belirlediği absolute yoldan yükleyip `require` için kaydedin.
`guard.open(trusted_config_json)` bir userdata oturumu döndürür. `read/check/close`
LuaJIT ile aynı davranır. `__gc` ve Lua 5.4 `<close>` desteği vardır.
Örnek: `samples/lua54/run_verified.lua`; tüketici testi: `tests/lua_consumer.lua`.

Lua kaynak kodu metin moduyla yüklenir. İmza güvenilirliği, betiğin işlevsel olarak
güvenli olduğunu veya verilen Lua environment'ın sandbox olduğunu kanıtlamaz.
[Lua 5.4 resmi API belgesi](https://www.lua.org/manual/5.4/manual.html).

## Python ve C/C++

Python bağlayıcısı `bindings/python/integrity_guard.py` içinde, yalnız standart
`ctypes` kullanır. `with Session(...):` veya `close()` ile oturumu bırakın.
`read(name)` doğrulanmış `bytes` döndürür; hata `IntegrityError.code` ile bildirilir.
C/C++ istemci header ve native `.lib` ile linklenir; örnek `samples/native-c`.

## Kapsam

Windows x64 ilk hedeftir. Linux/macOS/ARM64 çıktısı, .NET Framework paketi,
Lua 5.1/5.2/5.3 C modülü bu sürümde sunulmaz. Native derleme dosya bütünlüğü
algoritmasını değiştirmez; süreç içi kontrol sonuçları değiştirilebilir ve
bellekteki verinin gizliliği garanti edilmez.
