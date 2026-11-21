GTA alt bölgesinde yeni İngilizce altyazı belirirken önceki Türkçe satır daha erken gizlenir. Tesseract’ın emin olamadığı uzun diyalogları da Windows OCR ikinci kez okuyabilir.

Türkçe altyazı kutusu alt bölgeye biraz daha yaklaştırıldı. Çevirinin başında tek başına kalan `??` temizlenir. Birden fazla bozuk parça içeren satırlar elenir.

Replik varsayılan olarak yerel OPUS-MT ile çevirir. Gemini isteğe bağlıdır. Gemini seçiliyken bağlantı kesilirse veya kullanılabilir bir yanıt zamanında gelmezse yerel model devreye girer.

**Tanı günlüğünü aç** bağlantısı, pencere yakalama, OCR ve çeviri durumunu gösterir. Günlük bilgisayarda tutulur. İngilizce ve Türkçe altyazı metinleri, ekran görüntüleri ve API anahtarı günlüğe yazılmaz.

Kısa bir İngilizce replik okunduktan sonra OCR metni geçici olarak kaybederse, hazır Türkçe yanıt 700 ms içinde gösterilebilir. Yeni bir İngilizce replik doğrulandığında eski yanıt elenir.

GTA alt bölgesinde `Wait`, `Go` ve `No` gibi kısa replikler yeterince net okunduğunda noktalama olmadan da ilk karede çeviriye alınır.

Tam kurulum ve küçük güncelleme, oyuncunun bilgisayarında İngilizce Windows OCR motorunu kontrol eder. Eksikse Windows Update üzerinden indirip kurar. Windows Update kullanılamıyorsa paketteki Tesseract ile çeviri çalışır.

GTA için **Alt** konumunda Tesseract kısa bir satırı okuyamadığında veya düşük güvenle okuduğunda, Replik büyütülmüş görüntüyü İngilizce Windows OCR ile de okur. İkinci okuyucu aynı altyazı konumu ve yazı biçimi filtrelerini kullanır.

**0.2.1 ve üzeri kuruluysa** uygulamadaki **Güncelle** düğmesini kullanın. **0.2.0** kullanıcıları Replik-Guncelleme-x64-r1.exe dosyasıyla yükseltebilir. İlk kurulum için Replik-Kurulum-x64.exe dosyasını kullanın.

Bu sürüm, açılışta ve açık kaldığı sürece altı saatte bir yeni kararlı sürümleri denetler. Yeni sürüm varsa pencerenin sağ üstünde Güncelle düğmesi görünür. Uyumlu küçük paket önceliklidir. İndirme tamamlanınca dosya doğrulanır ve kurulum sihirbazı açılır.

GTA 6 topluluk paketi kurulum sırasında CouchSubs’ın resmî kataloğundan alınır. Replik dosyanın SHA-256 özetini doğrular ve kullanıcı hesabında saklar. Eşleşen satırlar doğrudan paketten, diğerleri seçilen motordan gelir. İnternet bağlantısı yoksa kurulum tamamlanır ve yerel model çalışır.
