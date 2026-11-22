# Replik

Windows 10 (2004 ve üzeri) ve Windows 11 x64 için İngilizce oyun altyazılarını yerel OPUS-MT veya Gemini ile Türkçeye çeviren masaüstü uygulaması.

## Kurulum ve kullanım

1. Deponun **Releases** bölümünden **Replik-Kurulum-x64.exe** dosyasını indirip çalıştır. Tesseract ve İngilizce modeli pakettedir. Hem tam kurulum hem küçük güncelleme, bilgisayarında eksikse İngilizce Windows OCR bileşenini Windows Update üzerinden kurar. Windows Update erişimi yoksa Replik Tesseract ile çalışır. Tam kurulum gerektiğinde Microsoft Visual C++ çalışma zamanını da kurar.
2. Oyunda İngilizce altyazıları aç. Replik varsayılan olarak **Alt** bölgesini okur. Altyazı ekranın ortasındaysa **Orta**, üstündeyse **Üst** seç.
3. Çeviri motorunu seç. Varsayılan **OPUS-MT** CPU üzerinde çalışır. Model, Python ve çeviri kütüphaneleri EXE'ye dahildir. **Gemini** için kendi anahtarını gir. **Çeviriyi başlat** düğmesine bas ve oyuna dön. **Oyuna dönünce bağlan** seçeneği öndeki Veyra veya Remote Play penceresini izler. Oyuncu tanınmazsa **Yenile** ile görüntüyü gösteren pencereyi listeden seç.
4. Son okunan İngilizce satır, Türkçe yanıt ve görüntü okuma / çeviri süreleri kontrol penceresinde görünür. Oyunda yeni bir replik algılandığında kutuda o repliğin çevirisi gösterilir. Önceki replik için geç gelen yanıt elenir. Başka bir uygulamaya geçtiğinde otomatik mod duraklar.

CouchSubs GTA 6 topluluk paketi tam kurulum ve küçük güncelleme sırasında resmî sunucudan alınır ve doğrulanır. Hazır Türkçe eşleşmeler ilk sıradadır. Pakette bulunmayan satırları varsayılan yerel OPUS-MT veya seçili Gemini çevirir. [Paket kaynağı](https://github.com/yorulmazsinan/couchsubs/tree/main/server/packs)

API anahtarı oturum boyunca bellekte tutulur. Seçilen ekran bölgesi yerel olarak okunur. Yalnızca Gemini seçiliyken pakette bulunamayan metinler Google'a gönderilir. Gemini zamanında kullanılabilir bir sonuç vermezse Replik yerel OPUS-MT modelini dener. Tekrarlanan satırlar oturum içinde önbellekten gösterilir.

**Tanı günlüğünü aç** bağlantısı, yakalama, OCR ve çeviri durumunu bilgisayarda tutulan bir dosyada gösterir. Günlüğe altyazı metni, ekran görüntüsü veya API anahtarı yazılmaz.

Replik, altyazı bölgesindeki görsel değişikliklerde OCR çalıştırır ve benzer görüntüleri 220 ms arayla yeniden denetler. **OCR ayarları** düğmesinden örnekleme aralığını, görüntü benzerliğini, sabit kare beklemeyi ve küçük metin farklarının nasıl işleneceğini ayarlayabilirsin. Ayarlar cihazda saklanır.

## Derleme

### Uygulama güncellemeleri

Replik 0.2.1 ve üzeri açılışta, ardından altı saatte bir yeni kararlı GitHub sürümünü denetler. Sağ üstteki düğmeyle elle de denetleyebilirsin. **Güncelle** uyumlu küçük paketi, çalışma zamanı değiştiğinde tam kurulumu indirir. Dosyanın boyutu ve SHA-256 özeti doğrulandıktan sonra Windows kurulum sihirbazı açılır. 0.2.0 kullanıcıları ilk geçişte Releases sayfasındaki küçük paketi kullanabilir.

### Windows EXE

Derleme için 64 bit Windows, .NET Framework 4.8, Windows 10 veya 11 SDK, Python/pip ve Inno Setup 6 gerekir. PowerShell'de `./build.ps1 -Compiler 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'` çalıştır. Kurulum dosyası `dist` klasörüne yazılır.
