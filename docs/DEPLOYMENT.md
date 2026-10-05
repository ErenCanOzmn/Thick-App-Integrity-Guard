# Dağıtım, obfuscation ve müşteri kabulü

## Hazırlık

Üretime dahil etmeden önce dosya envanterini, kritik işlem noktalarını, OS/runtime
desteğini, çevrimdışı davranışı, hata tepkisini ve onarım sürecini müşteriyle belirleyin.
Örnek uygulamadaki varsayımlar doğrudan müşteri uygulamasının gereksinimleri sayılmaz.

Her müşteri/ürün için anahtar yaşam döngüsü tanımlayın. Demo anahtarını üretimde
kullanmayın. Private key kaynak depoya, NuGet paketine veya uygulama kurulumuna girmez.
Public key uygulamanın güvenilen dağıtımına sabitlenir; public key gizli değildir.

İmzalama aracı yerel PEM dosyası okur. HSM/KMS entegrasyonu, anahtar erişim denetimi,
rol ayrımı ve imzalama audit logları bu projede uygulanmamıştır; üretim altyapısına
uyarlamanız gerekir.

## Yayın sırası

1. Public key ve gerekli dosya listesiyle uygulamayı derleyin.
2. Uygulamaya ve doğrulama entegrasyonuna uygun obfuscation işlemlerini uygulayın.
3. Varsa Authenticode veya dosya baytlarını değiştiren son dağıtım işlemlerini tamamlayın.
4. Son dosyaların manifestini oluşturup artan pozitif release değeriyle imzalayın.
5. İmzalı son çıktıyı ve gerçek kullanıcı işlemlerini test edin.
6. Dosyaları, `integrity.json` ve `integrity.sig` ile birlikte güvenilen kanal üzerinden dağıtın.

Obfuscation'ın native export isimlerini, public API'yi, JSON alanlarını veya
reflection kullanan uygulama parçalarını kırmadığını doğrulayın. İstisnaları
obfuscator yapılandırmasında tanımlayın. Bu repo bir obfuscation aracı içermez.
Obfuscation sonrası test edilmeyen düz derlemenin sonuçları üretim çıktısına kanıt değildir.

Manifest imzalandıktan sonra korunan dosyalar değişirse doğrulama başarısız olur.
Private key'i uygulamaya gömerek veya doğrulama hatasını yok sayarak bunu çözmeyin.

## İmzalama örneği

Önce aracı derleyin:

```powershell
dotnet build tools/ManifestSigner -c Release
```

Yalnız yerel geliştirme için yeni anahtar oluşturma:

```powershell
dotnet tools/ManifestSigner/bin/Release/net8.0/ManifestSigner.dll keygen .demo-keys
```

Mevcut anahtar dosyalarının üzerine yazılmaz. Kendi dağıtımınızı imzalama komutunun
biçimi aşağıdadır; yollar örnektir, private key korunan imzalama ortamındadır:

```powershell
dotnet tools/ManifestSigner/bin/Release/net8.0/ManifestSigner.dll sign C:/SecureSigning/private.pem C:/Release/MyApp 2 rules.json
```

Bu örnekte yalnız `rules.json` korunur. EXE/DLL de dahil edilecekse hem imzalayıcıya
hem uygulamadaki `RequiredFiles` listesine aynı tam envanteri verin. İmzalayıcı
sıfır olmayan exit code döndürürse çıktıyı yayımlamayın.

## Sürüm ve güncelleme

Eski imzalı sürümü reddetmek için minimum sürüm güvenilen host/servisten gelmelidir.
Değiştirilebilir uygulama klasöründeki yerel bir değer tüm klasörle birlikte geri
alınabilir. Sunucuya erişilemiyorsa müşteri sözleşmesinde belirlenen davranışı
uygulayın; minimum sürümü sessizce düşürmeyin.

Çalışan uygulamanın dosyalarını parça parça değiştirmeyin. Ayrı staging dizininde
doğrulayın; uygulamayı durdurup güvenilir updater üzerinden geçiş yapın.
Updater/kurulum güvenliği bu kütüphanenin dışında tasarlanmalıdır.

Tek sabitlenmiş public key desteklenir. Otomatik key ring, iptal servisi ve rotasyon
yoktur. Anahtar değişimi güvenilen uygulama güncellemesiyle birlikte yapılmalıdır.
Anahtar sızıntısında yeni güvenilir anahtar/doğrulayıcı dağıtımı ve minimum sürüm
politikasının yenilenmesi gerekir.

## Üretim kabul listesi

- Temiz kurulum, güncelleme ve onarım akışları gerçek müşteri ortamında çalışıyor.
- Değiştirilmiş içerik/imza, eksik dosya ve erişim hatası işlemi engelliyor.
- Eski geçerli imzalı sürüm, güvenilen minimum politika altında reddediliyor.
- Dosya onarımı aynı oturumu açmıyor; yeniden başlatma sonrası kontrol yapılıyor.
- İşlem yalnız doğrulanmış kopyayı kullanıyor; şema/iş kuralları ayrıca doğrulanıyor.
- Obfuscation uygulanmış son ürün test edildi; export/API/JSON sözleşmeleri korunuyor.
- İmzalama anahtarı kurulum paketinde veya kaynak depoda bulunmuyor.
- Gerçek dosya boyutlarında açılış, periyodik ve işlem öncesi gecikme ölçüldü.
- Log kuyruğu büyümesi, timeout ve yanlış alarm desteği tanımlandı.
- Sunucu yetkilendirmesi istemcinin `Success`/`Allowed` bayrağına dayanmıyor.

Bu kontroller müşteri projesine özel geliştirme ihtiyacını ortadan kaldırmaz.
Runtime gözlemi/bellek müdahalesi bu kütüphanenin koruma kapsamı değildir.
