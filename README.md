# Replik

Windows 10 (2004 ve üzeri) ve Windows 11 x64 için İngilizce oyun altyazılarını Gemini ile Türkçeye çeviren masaüstü uygulaması.

## Kurulum ve kullanım

1. Deponun **Releases** bölümünden **Replik-Kurulum-x64.exe** dosyasını indirip çalıştır. Kurulum İngilizce OCR modelini ve Tesseract motorunu içerir. Gerektiğinde Microsoft Visual C++ çalışma zamanını da kurar.
2. Remote Play'i aç ve oyun içindeki İngilizce altyazıları etkinleştir. Replik'te oyun penceresini seçip **Altyazı alanını çiz** düğmesine bas. Açılan sabit görüntüde kutuyu içinden tutup taşı, kenar veya köşelerinden boyutlandır ya da kutunun dışından sürükleyerek yeni alan çiz. **Uygula** seçimi kaydeder. **Esc** önceki alanı korur. Aynı oturumda **Altyazı alanını düzenle** ile geri dönebilirsin.
3. Kendi Gemini API anahtarını gir. Gerekirse hesabında kullanılabilen bir Gemini metin modelinin adını yaz. **Çeviriyi başlat** düğmesine bas.
4. Oyun penceresini önde ve seçilen alanı açıkta tut. Türkçe satır, seçtiğin alanın hemen üstünde veya altında görünür. Replik'e dönünce okuma duraklar. Oturumu bitirmek için **Durdur** düğmesini kullan.

Hızlı konuşmalarda uygulama, OCR'ın yüksek güvenle okuduğu tek karelik replikleri de sıraya alır. Gemini istekleri aynı anda en fazla üç satır için çalışır. Yanıtlar konuşma sırasıyla, beyaz yazılı yarı saydam bir kutuda gösterilir. Yoğun diyalogda iki kısa replik birlikte görünebilir.

API anahtarı oturum boyunca bellekte tutulur. Seçilen ekran bölgesi yerel olarak okunur. Gemini'ye algılanan metin gönderilir. Tekrarlanan satırlar oturum içinde önbellekten gösterilir. Yeni satırlardaki gecikme OCR, ağ ve Gemini yanıt süresine bağlıdır. Bu ilk test sürümünün GTA 6 ve Remote Play üzerindeki uyumluluğu henüz doğrulanmadı.

## Derleme

64 bit Windows, .NET Framework 4.8 ve Inno Setup 6 gerekir. PowerShell'de `./build.ps1 -Compiler 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'` çalıştır. Kurulum dosyası `dist` klasörüne yazılır.
