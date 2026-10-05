# Çalıştırılmış örnek uygulamalar

2026-10-06 tarihinde Windows üzerinde gerçek EXE süreçleriyle doğrulandı.
Bu çalışma yalnız kütüphane unit testlerinden oluşmaz: uygulamalar ayrı süreçlerde
başlatıldı, yalnız test kopyalarının dosyaları değiştirildi ve uygulama çıktıları okundu.

## WinForms teklif uygulaması

Çalıştırılabilir dosya: `artifacts/demo/DemoApp.exe`.
Klasördeki DLL, runtimeconfig/deps, kurallar, manifest ve imza dosyaları birlikte
kalmalıdır. Çalıştırmak için Windows .NET 8 Desktop Runtime gerekir.

Pencere, imzalı kurallardan teklif hesaplar. Açılışta, beş saniyede bir ve işlem
öncesinde kontrol yapar. İlk başarısızlık sonrası teklif düğmesi oturum boyunca
engellenir. Bu demoda altı dağıtım dosyası manifestte yer alır; doğrulama yüklü
koda güvenilir başlangıç veya bellek bütünlüğü garantisi vermez.

GUI testinde gizli başlatılan pencereye UI Automation erişemedi. Ardından
Windows pencere/kontrol API'siyle aynı uygulamanın gerçek kontrolleri gözlendi,
teklif düğmesine tıklama mesajı gönderildi ve modal diyalog okundu. Uygulamaya
test sonuçlarını taklit eden bir çalışma modu eklenmedi.

| Kontrol | Gözlenen sonuç |
| --- | --- |
| Temiz açılış | `DOGRULANDI`, düğme açık |
| Gerçek teklif düğmesi | Diyalogda `Demo hizmet: 125,00 TL` |
| Çalışırken kurallar değişti | `FileMismatch`, düğme kapalı |
| Dosya aynı oturumda onarıldı | Altı saniye sonra da düğme kapalı |
| Onarım sonrası yeniden başlatma | Doğrulama başarılı, düğme açık |
| Değiştirilmiş dosyayla açılış | `FileMismatch`, düğme kapalı |

GUI raporu: `artifacts/application-tests/gui-<run-id>/gui-report.json`.

## Konsol teklif uygulaması

Çalıştırılabilir dosya: `artifacts/apps/QuoteConsole/QuoteConsole.exe`.
Klasörün tamamını koruyun; .NET 8 Runtime gerekir. Konsola şu komutları yazın:

```text
quote 2
check
exit
```

Çıktı JSON Lines biçimindedir. Temiz uygulamada iki ürün için toplam `251.00`
hesaplanır. `ProtectedRules` müşteri örneğini doğrudan kullanır; bu uygulamada
imzalanan kapsam yalnız `rules.json` dosyasıdır, EXE/DLL dosyaları değildir.
Dosya bütünlüğü başarılı olsa bile ürün/fiyat şeması ayrıca doğrulanır.

Gerçek süreç testlerinin 7/7'si geçti:

- Temiz teklif hesabı ve negatif miktarın reddi.
- Çalışırken dosya değişikliği → `FileMismatch`; onarım → `SessionBlocked`;
  yeniden başlatma → başarılı teklif.
- Değiştirilmiş dosyayla açılış → `FileMismatch`, exit 1.
- Eksik dosyayla açılış → `VerificationError`, exit 1.
- Bozulmuş imzayla açılış → `InvalidSignature`, exit 1.
- Geçerli imzalı ama fiyat alanı eksik veri → `InvalidBusinessRules`.
- WinForms EXE `--verify`: temiz exit 0, değiştirilmiş dosya exit 1.

Süreç raporu: `artifacts/application-tests/<run-id>/process-report.json`.
Raporlar yalnız denenen kontrollerin sonuçlarıdır; Frida/bellek müdahalesi testi değildir.

## Yeniden derleme ve test

```powershell
.\build-example-apps.ps1
```

Bu betik demo anahtarı gerekiyorsa oluşturur, uygulamaları derler ve imzalar,
kütüphane regresyon testlerini ve iki gerçek uygulamanın testlerini çalıştırır.
GUI testleri kullanıcı masaüstü oturumu ve Windows pencere API'lerine erişim
gerektirir; headless servis oturumunda başarısız olabilir. Testler sadece kendi
başlattıkları süreçleri kapatır ve benzersiz test kopyalarını değiştirir.
Test kopyaları inceleme için artifacts altında bırakılır. Anahtarlar paylaşılmaz.

Native C/Lua örnekleri bu testlere dahil değildir. Gerçek NativeAOT DLL ve Lua
runtime doğrulaması henüz tamamlanmadı. Bunların gereksinimleri ve ayrı kabul
adımları [native entegrasyon rehberinde](NATIVE.md) açıklanır.
