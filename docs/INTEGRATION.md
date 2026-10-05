# .NET entegrasyonu

## Hedef ve kurulum

Managed paket .NET 8'i hedefler; doğrulanmış kullanım Windows üzerindedir.
WinForms veya konsol örneklerinden uygulamanızın mimarisine uygun olanı inceleyin.
WPF/servis entegrasyonunda zamanlama ve hata tepkisini kendi uygulamanız yönetir.
.NET Framework 4.x uyumlu bir managed paket değildir.

`src/IntegrityGuard/IntegrityGuard.csproj` için `ProjectReference` ekleyin.
Alternatif olarak `build-package.ps1` ile oluşan `IntegrityGuard.0.3.0.nupkg`
dosyasını kurumunuzun yerel paket kaynağına alın. Genel NuGet sunucusunda bu isimle
bir paketin bu projeye ait olduğunu varsaymayın; bu çalışma paket yayımlamaz.

## Politika ve oturum

`IntegrityPolicy` kök dizini, public key'i, zorunlu dosya listesini ve minimum
sürümü içerir. Kurucuda dosya listesi kopyalanır. Geçersiz temel yapılandırma
`ArgumentException` veya türevini üretir; uygulama özelliği açmadan durmalıdır.
PEM anahtarı kontrol sırasında ayrıştırılır; geçersiz anahtar doğrulamayı başarısız kılar.

`IntegritySession` uygulama oturumu boyunca tutulmalıdır. Her işlemde yeniden
oluşturmak başarısızlık engelini sıfırlar ve önerilen entegrasyonu bozar.

| Kontrol aşaması | Host'un sorumluluğu |
| --- | --- |
| `Startup` | Özelliği kullanılabilir hale getirmeden önce çağırmak |
| `Periodic` | İsteğe bağlı timer üzerinden çağırmak; aralık ve iptali yönetmek |
| `BeforeOperation` | Her korunan işlemden hemen önce çağırmak |

Üçü de tam dosya kontrolüdür; isimler olayın aşamasını belirtir. Kütüphane kendi
timer'ını veya ağ servisini başlatmaz. GUI'de disk I/O için uygun arka plan
çalışması kullanın. Periyodik kontrol işlem öncesi kontrolün yerine geçmez.

İlk başarısızlıktan sonra session engellenir ve sonraki çağrılarda `SessionBlocked`
döner. Oturum içinde engeli sessizce kaldırmayın. Kullanıcıya güvenilen kurulumdan
onarım ve yeniden başlatma yolunu sağlayın.

## Doğrulanmış verinin kullanılması

Başarılı sonuçtaki `Files["rules.json"]` doğrulanmış bayt kopyasıdır. Kaynağı
diskten yeniden açmayın. JSON şemasını ve uygulamanın alan sınırlarını ayrıca
doğrulayın. İmza, iş verisinin mantıksal olarak geçerli olduğu anlamına gelmez.

Kontroller kilitle sıralanır fakat doğrulama ve iş işlemi birlikte atomik değildir.
Host iş akışını sıralamalı ve kopyayı yalnız ilgili işlemde kullanmalıdır.
`byte[]` çağıran tarafından değiştirilebilir; paylaşılan değiştirilebilir global
önbelleğe koymayın. Her kontrolden gelen kopya bağımsızdır.

`samples/CustomerIntegration/ProtectedRules.cs` bu davranışın derlenebilir örneğidir.
Düşük seviyeli `IntegrityVerifier.Verify` stateless API'si de vardır; kendi başına
oturum engeli sağlamaz.

## Olaylar ve hata tepkisi

İsteğe bağlı `Action<IntegrityObservation>` alıcısı şu alanları alır:
`SchemaVersion`, `CheckId`, `TimestampUtc`, `Checkpoint`, `Code`, `Allowed`,
`DurationMilliseconds`. Dosya içeriği, private key, kullanıcı adı veya yerel yol yoktur.
Olaylar yerel telemetridir; sunucu yetkilendirme/attestation kanıtı değildir.

Alıcı hızlı dönmeli, aynı session'a geri çağrı yapmamalı ve mümkünse sınırlı bir
log kuyruğuna yazmalıdır. Callback için timeout yoktur. Alıcı istisnaları sayılır
(`ObserverFailureCount`) ve doğrulama kararını değiştirmez. Eşzamanlı çağrılarda
olay teslim sırası garanti edilmez. Saklama ve teslim politikasını müşteri belirler.

| Kod | Açıklama |
| --- | --- |
| `Verified` | Doğrulanmış kopya kullanılabilir; iş şeması ayrıca kontrol edilir |
| `InvalidConfiguration` | Entegrasyon politikasını düzeltin |
| `InvalidSignature` | Güvenilen anahtar/dağıtım eşleşmesini kontrol edin |
| `InvalidManifest`, `InvalidDigest`, `MissingRequiredFile` | Manifest/envanter hatası |
| `FileMismatch` | Dosya imzalı özetle eşleşmiyor |
| `RollbackRejected` | Sürüm güvenilen minimumun altında |
| `VerificationError` | Erişim, eksik dosya, boyut limiti veya kriptografi hatası |
| `SessionBlocked` | Önceki başarısızlık nedeniyle oturum engellenmiş |

Bu kodlar saldırı kanıtı değildir; yarım kalmış güncelleme ve erişim sorunları da
hata yaratabilir. Başarı dışındaki sonuçlarda ilgili işlemi sürdürmeyin.

## Sınırlar

- Alt dizinsiz 1–32 dosya: ASCII harf/rakam/nokta/tire/alt çizgi adları.
- Dosya başına 32 MiB, toplam 64 MiB; manifest 64 KiB.
- Manifest ve requiredFiles listesi tam eşleşir; kısmi envanter kabul edilmez.
- ECDSA P-256 / SHA-256; manifestin tam baytları imzalanır. İmza P1363 biçimindedir.
- Dosya doğrulaması süreç belleğini, yüklenmiş kodu veya debugger durumunu doğrulamaz.
- Public key ve minimum sürüm değiştirilebilir dosyadan sorgusuz alınmamalıdır.
- Native/Lua için [ayrı arayüz sözleşmesi](NATIVE.md) geçerlidir.
