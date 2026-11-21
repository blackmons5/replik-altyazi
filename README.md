# Replik

Windows 10 (2004 ve üzeri) ve Windows 11 x64 için İngilizce oyun altyazılarını Gemini ile Türkçeye çeviren masaüstü uygulaması.

## Kurulum ve kullanım

1. Deponun **Releases** bölümünden **Replik-Kurulum-x64.exe** dosyasını indirip çalıştır. Kurulum İngilizce OCR modelini ve Tesseract motorunu içerir. Gerektiğinde Microsoft Visual C++ çalışma zamanını da kurar.
2. Oyunda İngilizce altyazıları aç. Replik varsayılan olarak **Alt** bölgesini okur. Altyazı ekranın ortasındaysa **Orta**, üstündeyse **Üst** seç.
3. Gemini API anahtarını gir, **Çeviriyi başlat** düğmesine bas ve oyuna dön. **Oyuna dönünce bağlan** seçeneği öndeki Veyra veya Remote Play penceresini izler. Oyuncu tanınmazsa **Yenile** ile görüntüyü gösteren pencereyi listeden seç.
4. Son okunan İngilizce satır, Türkçe yanıt ve görüntü okuma / Gemini süreleri kontrol penceresinde görünür. Çevrilen satırlar konuşma sırasıyla gösterilir. Art arda gelen iki replik aynı kutuda görünebilir. Başka bir uygulamaya geçtiğinde otomatik mod duraklar.

OCR'ın kabul ettiği yeni satır doğrudan çeviri sırasına girer. En fazla üç Gemini isteği aynı anda çalışır. Sıradaki yanıt geldiğinde yeni ekran karesi beklenmeden gösterilir. Yerel önbellekte bulunan aynı satır için yeni istek yapılmaz.

API anahtarı oturum boyunca bellekte tutulur. Seçilen ekran bölgesi yerel olarak okunur. Gemini'ye algılanan metin gönderilir. Tekrarlanan satırlar oturum içinde önbellekten gösterilir. Yeni satırlardaki gecikme OCR, ağ ve Gemini yanıt süresine bağlıdır. Bu ilk test sürümünün GTA 6 ve Remote Play üzerindeki uyumluluğu henüz doğrulanmadı.

## Derleme

64 bit Windows, .NET Framework 4.8 ve Inno Setup 6 gerekir. PowerShell'de `./build.ps1 -Compiler 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'` çalıştır. Kurulum dosyası `dist` klasörüne yazılır.
