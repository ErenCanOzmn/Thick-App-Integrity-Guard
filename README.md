# Thick App Integrity Guard

Windows/.NET thick-client uygulamalarına imzalı dosya bütünlüğü kontrolü eklemek
için geliştirilmiş bir referans kütüphane ve entegrasyon örnekleri.

Amaç; uygulamanın kullandığı kuralların, betiklerin veya dağıtım dosyalarının
beklenen, imzalı sürümle eşleştiğini kontrol etmek ve doğrulama başarısızsa ilgili
işlemi durdurmaktır. Doğrulanan dosyanın içeriği bellekte bir kopya olarak döndürülür;
uygulama bu kopyayı kullanır.

## Entegrasyondan önce okuyun

**Bu proje doğrudan müşterinin uygulamasına eklenip üretime çıkarılacak hazır bir
güvenlik ürünü değildir. Kendi projenize dahil etmeden önce, küçük de olsa projeye
özel geliştirme, yapılandırma ve kabul testleri gerekir.** Gereken çalışma;
uygulamanın mimarisine, kullandığı dile, güncelleme yöntemine ve çevrimdışı çalışma
ihtiyacına göre daha kapsamlı olabilir.

**Üretim kullanımında uygulamanızın ve doğrulama entegrasyonunun obfuscation
sürecine dahil edilmesi planlanmalıdır.** Seçilen obfuscation ayarlarının mevcut
uygulama, JSON şeması, NativeAOT ve varsa native export isimleriyle uyumluluğunu
test edin. Obfuscation tersine incelemeyi zorlaştıran ek bir katmandır; dosya
imzasının, sunucu yetkilendirmesinin veya güvenilir güncellemenin yerine geçmez.
Frida'yı ya da süreç belleğinin okunmasını kesin olarak engellemez.

Bu repo **anti-Frida, anti-debug veya çalışma zamanı bellek koruması sağlamaz**.
Kontrollerin çalıştığı süreci kontrol eden biri yerel kontrol akışını veya
sonuçlarını değiştirebilir. İstemcide kullanılmak zorunda olan verinin görünmez
kalacağına dair bir garanti verilmez.

## Nasıl çalışır?

1. Dağıtılacak dosyaların SHA-256 özetleri bir manifestte toplanır.
2. Manifest ECDSA P-256 private key ile imzalanır.
3. Uygulama, kendisine sabitlenmiş public key ile manifest imzasını doğrular.
4. Dosyalar okunup özetleri karşılaştırılır; sürüm politikası kontrol edilir.
5. Başarılı kontrolde doğrulanmış bayt kopyaları kullanıma sunulur.
6. Oturum API'sinde ilk başarısız kontrol ilgili oturumu engeller. Dosyayı onarmak
   aynı oturumu açmaz; güvenilen onarımın ardından uygulama yeniden başlatılır.

WinForms örneği açılışta, beş saniyede bir ve teklif işleminden hemen önce kontrol
yapar. Beş saniyelik aralık bir tespit penceresidir; aradaki müdahaleyi önleyen bir
garanti değildir. İşlem öncesi kontrol ve doğrulanmış kopyayı kullanmak gereklidir.
Başlangıç kontrolü de uygulama kodu yüklenip çalışmaya başladıktan sonra yapılır;
güvenilir yükleme öncesi doğrulama değildir.

## Destek ve doğrulama durumu

| Bileşen | Durum |
| --- | --- |
| Windows / .NET 8 kütüphanesi | Derlendi; 23 regresyon testi geçti |
| WinForms ve konsol örnekleri | Gerçek EXE süreçlerinde 7 senaryo ve 6 GUI kontrolü geçti |
| Native C ABI v1 kaynakları | NativeAOT hedefi eklendi; managed ortamda 12 giriş noktası testi geçti |
| Windows x64 native DLL | Bu sürüm için gerçek NativeAOT çıktı doğrulaması tamamlanmadı |
| Lua 5.4, LuaJIT 2.1, Python native bağlayıcıları | Kaynak ve örnekler var; gerçek native DLL ile runtime doğrulaması tamamlanmadı |
| Linux / macOS / ARM64 / .NET Framework | Bu sürümün doğrulanmış destek kapsamında değil |

Native kaynak bulunması, hazır veya test edilmiş bir native DLL bulunduğu anlamına
gelmez. Native/Lua entegrasyonunu müşteri ortamında derleyip doğrulamadan dağıtmayın.
Test sonuçları dosya bütünlüğü davranışını gösterir; süreç içi müdahaleye dayanıklılık
veya üretim sertifikasyonu değildir.

## Hızlı başlangıç

Windows, .NET 8 SDK ve WinForms için Windows Desktop targeting pack gerekir.
Gerçek uygulama testlerinde ayrıca Python 3 kullanılır; harici Python paketi gerekmez.

```powershell
git clone https://github.com/ErenCanOzmn/Thick-App-Integrity-Guard.git
cd Thick-App-Integrity-Guard

# Demo anahtarını yerelde üretir, WinForms örneğini derler ve dosyalarını imzalar.
.\build-demo.ps1
.\artifacts\demo\DemoApp.exe
```

Pencerede `DOGRULANDI` görülünce **Demo teklif hesapla** düğmesi kullanılabilir.
Örnek veri değişirse `FileMismatch` sonucu oluşur ve düğme kapanır.
EXE'yi klasöründeki DLL, manifest, imza ve yapılandırma dosyalarından ayırmayın.

```powershell
# WinForms + konsol uygulaması, gerçek süreç ve GUI testleri:
.\build-example-apps.ps1

# Konsol örneği; quote 2, check ve exit komutlarını kabul eder:
.\artifacts\apps\QuoteConsole\QuoteConsole.exe

# Yerel .NET NuGet paketi; genel bir sunucuya yayın yapmaz:
.\build-package.ps1
```

Demo anahtarları `.demo-keys/` altında yalnız sizin bilgisayarınızda oluşturulur.
**Demo private key'i müşteriye göndermeyin; üretimde yeniden kullanmayın.**

## Kendi .NET projenize dahil etme

1. `src/IntegrityGuard/IntegrityGuard.csproj` projesine referans ekleyin veya
   `build-package.ps1` ile üretilen paketi kurumunuzun özel NuGet kaynağından kullanın.
2. Korunacak dosyaları açıkça belirleyin. Uygulamadaki liste manifestle tam eşleşmelidir.
3. Müşteriye/ürüne ait public key'i güvenilen uygulama yapılandırmasına sabitleyin.
4. Uygulama oturumu başına tek `IntegritySession` oluşturun.
5. Açılışta ve her korunan işlemden önce aynı oturumla doğrulayın.
6. Diskten yeniden okumak yerine dönen doğrulanmış baytları kullanın.

```csharp
using IntegrityGuard;

// Uygulama açılırken bir kez oluşturun. Key ve minimum sürüm güvenilen kaynaktan gelir.
var session = new IntegritySession(new IntegrityPolicy(
    AppContext.BaseDirectory,
    pinnedPublicKeyPem,
    new[] { "rules.json" },
    minimumRelease: trustedMinimumRelease));

var startup = session.Check(IntegrityCheckpoint.Startup);
if (!startup.Success)
    return; // İlgili özelliği açmayın; kullanıcıya onarım yolunu gösterin.

// Her korunan işlemden önce, aynı session ile:
var result = session.Check(IntegrityCheckpoint.BeforeOperation);
if (!result.Success)
    return;

byte[] authenticatedRules = result.Files["rules.json"];
// Bu kopyayı ayrıştırın ve uygulamanızın veri şemasını/iş kurallarını da doğrulayın.
```

Çalışan entegrasyon örneği: [ProtectedRules.cs](samples/CustomerIntegration/ProtectedRules.cs).
API, hata kodları ve oturum davranışı: [Entegrasyon rehberi](docs/INTEGRATION.md).

## Müşterinin tamamlaması gereken uyarlamalar

- **Korunacak kapsam:** Gerçek dosya listesi, dosya boyutları ve kritik işlem noktaları.
- **Obfuscation:** Uygulamanın desteklediği koruma aracı ve ayarları; son ürün üzerinde
  uyumluluk ve performans testleri. Bu repo obfuscator içermez.
- **Anahtar yönetimi:** Üretim private key'inin korunan imzalama ortamında tutulması;
  public key dağıtımı, rotasyon ve sızıntı sonrası kurtarma süreci.
- **Güncelleme:** İmzalı dağıtım, güvenilir minimum sürüm politikası ve yarım kalan
  güncelleme/onarım akışı. Kütüphane otomatik updater sağlamaz.
- **Hata tepkisi:** Hangi özelliğin engelleneceği, kullanıcı mesajı ve destek süreci.
  Loglama başarısızlığı bir doğrulama hatasını başarıya dönüştürmemelidir.
- **İş kuralları:** İmzalı verinin geçerli şemaya sahip olması ayrıca kontrol edilmelidir.
- **Sunucu sınırı:** Yetki, lisans, ödeme ve benzeri kritik kararların sunucuda
  değerlendirilmesi; istemcinin `Success=true` değerinin kanıt kabul edilmemesi.
- **Kabul testleri:** Gerçek OS/runtime, kurulum dizini, dosya boyutları, çevrimdışı
  davranış ve performans üzerinden müşteriye özgü doğrulama.

## Üretim dosyalarının hazırlanma sırası

**Derle → obfuscation/son dosya dönüşümleri → varsa kod imzalama → manifest
oluşturma ve imzalama → son çıktıyı doğrulama → dağıtım.**

Manifest imzalandıktan sonra kapsamdaki dosyayı değiştirmeyin. Obfuscation veya
kod imzalama dosyayı değiştiriyorsa bunlar manifestten önce tamamlanmalıdır.
Public key sır değildir; ancak public key'in ve doğrulama akışının güvenilir
dağıtımını ayrıca ele almak gerekir.

İmzalama ve kabul adımları: [Dağıtım rehberi](docs/DEPLOYMENT.md).

## C/C++, Lua ve Python

`src/IntegrityGuard.Native` Windows x64 için NativeAOT C ABI hedefidir.
`native/include/integrity_guard.h` oturum açma, kontrol, doğrulanmış dosyayı
kopyalama ve oturum kapatma fonksiyonlarını tanımlar.

Native derleme için Visual Studio C++ Build Tools, Windows SDK, .NET 8 SDK,
CMake ve testler için Python 3 gerekir:

```powershell
.\build-native.ps1
```

Bu betik native çıktı ve dış istemci testleri başarılı olmadan başarı bildirmez.
Lua 5.4 için ek C modülü, LuaJIT için FFI bağlayıcısı gerekir.
[Native entegrasyon rehberi](docs/NATIVE.md) platform, bellek ve çalışma zamanı
testlerinin durumunu ayrıntılandırır.

## Repo içeriği

```text
src/IntegrityGuard/          .NET dosya bütünlüğü kütüphanesi
src/IntegrityGuard.Native/   NativeAOT C ABI kaynakları
src/DemoApp/                 WinForms örneği
tools/ManifestSigner/        Geliştirme/entegrasyon imzalama aracı
native/include/              C header
bindings/                   Lua 5.4, LuaJIT ve Python bağlayıcıları
samples/                    Müşteri entegrasyonu, konsol ve native örnekler
tests/                      Regresyon, ABI ve uygulama kabul örnekleri
docs/                       Entegrasyon, dağıtım ve test açıklamaları
```

Bu depoda private key, derlenmiş müşteri dosyaları, iç çalışma notları veya
çalışma zamanı kontrol atlatma denemeleri bulunmaz.

## Kullanım koşulları

Bu aşamada lisans dosyası eklenmemiştir. Müşteri kullanım ve dağıtım koşulları
proje sahibiyle ayrıca belirlenmelidir.
