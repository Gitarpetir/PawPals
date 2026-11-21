# PawPals — server gratis dengan alamat tetap

Persiapan hosting, 21 November 2026. Patch untuk source 0.10 yang sudah memakai UI pixel.
Ini bukan project baru dan belum berarti server publik sudah berjalan.

## 1. Pasang patch ke project yang sekarang

1. Tutup PawPals dan hentikan debugging/server lokal agar file build tidak terkunci.
2. Buat salinan folder kerja sekarang sebagai backup.
3. Buka ZIP patch. Salin **isi ZIP**, mengikuti susunan foldernya, ke folder kerja yang berisi `PawPals.slnx`.
4. Gabungkan folder `PawPals.Desktop`, `PawPals.Server`, dan `PawPals.Server.Checks`. Ganti file bernama sama.
5. Buka solution yang sama dan Rebuild. Jangan membuat project lain atau menghapus folder lama.

Patch berisi source. Menjalankan exe portable lama tidak memakai perubahan ini: jalankan source yang dibangun ulang, atau buat portable baru dengan `BUILD_PORTABLE.cmd` setelah pemasangan.
Panel pixel dan tombol overlay tidak diganti oleh patch server ini.

## 2. Hasil yang dituju

| Bagian | Tempat berjalan |
| --- | --- |
| Aplikasi dan kucing | Laptop tiap pemain; mode sendiri tetap bisa tanpa internet/server |
| Multiplayer ASP.NET Core + SignalR | Satu Web Service **Render Free** |
| Kode room | **Neon Free PostgreSQL** |
| History chat | Tetap di profil lokal tiap pemain, sesuai aturan 0.10 |

Laptop Alfi dan terminal Cloudflare tidak diperlukan setelah deployment berhasil.
URL `https://NAMA-LAYANAN.onrender.com` adalah contoh; URL asli diberikan Render.
Alamat tetap dipakai selama layanan/hostname yang sama dipertahankan. Kode room masih terpisah dari alamat server.

Render Free tidur setelah 15 menit tanpa trafik masuk dan umumnya membutuhkan sekitar satu menit untuk bangun. Client menunggu kesiapan `/health` sampai maksimal dua menit. Ia tidak mengulang Create/Join secara otomatis dan tidak auto-join ketika aplikasi dibuka.
Menutup panel Room membatalkan koneksi yang masih menunggu.

## 3. Siapkan akun gratis

Daftar/login ke [Neon](https://console.neon.tech/) dan [Render](https://dashboard.render.com/), bisa memakai akun GitHub.
Pilih **Free**. Tidak perlu membeli domain atau memilih database Render berbayar.
Neon Free bukan trial terbatas waktu. Render Free Postgres justru kedaluwarsa setelah 30 hari, sehingga tidak dipakai dalam rancangan ini.

Gratis tetap memiliki kuota. Render menyertakan 750 jam instance per workspace per bulan dan batas bandwidth/build; Neon memiliki batas compute/penyimpanan/transfer.
Jangan meningkatkan paket menjadi berbayar untuk mengikuti panduan ini. Jika penyedia meminta verifikasi pembayaran yang tidak ingin dilakukan, hentikan langkah pembuatan layanan di sana dan laporkan layar tersebut agar opsi ditinjau.
Tidak ada janji server 24/7, kapasitas gratis tanpa batas, atau kinerja 10 pemain sebelum pengujian di hosting.

## 4. Buat database Neon

1. Buat project `pawpals`, paket Free. Gunakan wilayah dekat Singapura bila tersedia.
2. Buka **Connect** dan pilih database yang dibuat. Pilih role pemilik project/database agar tabel dapat dibuat.
3. Salin connection string URI PostgreSQL, bentuknya `postgresql://...`. Salin nilainya saja, tanpa awalan `psql` atau tanda kutip pembungkus.
4. Nilai ini adalah rahasia server. Nanti masukkan sebagai `DATABASE_URL` pada pengaturan environment Render, bukan di source, release ZIP, aplikasi pemain, chat, atau GitHub.

Server mendukung URI Neon direct maupun pooled, memverifikasi sertifikat TLS untuk host database jarak jauh, dan membuat tabel `pawpals_room_catalog` sendiri. Tidak perlu menulis SQL untuk pemakaian biasa.
Tabel menyimpan **kode room saja**, bukan nama, token anggota, animasi, atau isi chat.

## 5. Siapkan source di GitHub

Render membangun **source**, bukan ZIP exe dari halaman Releases.
Di root repository, siapkan susunan ini dari project yang sudah dipatch:

- `Dockerfile`, `.dockerignore`, `render.yaml`, dan `.gitignore`.
- Folder `PawPals.Server` dengan file source dan `.csproj`-nya.
- Folder `PawPals.Desktop` cukup berisi **`CatBehavior.cs`** untuk kebutuhan server.

Server memakai satu file perilaku kucing dari desktop. Aset kucing, font, file XAML, dan exe Windows tidak perlu diunggah untuk membangun server.
Jangan unggah `bin`, `obj`, `.vs`, file JSON data/sesi, kredensial, atau ZIP aplikasi ke folder source.
Repository yang sudah berisi seluruh source juga bisa dipakai selama `Dockerfile` berada sejajar dengan kedua folder project tersebut.

Jika menggunakan antarmuka GitHub: buka tab **Code → Add file → Upload files**, unggah susunan folder di atas, lalu commit. Releases tetap dipakai untuk ZIP aplikasi pemain.
Pastikan file tersembunyi `.dockerignore` dan `.gitignore` ikut ada. Jika menggunakan Git Bash, keduanya disertakan oleh Git ketika ditambahkan dari folder repository.

## 6. Buat layanan Render

Cara dengan Blueprint:

1. Pilih **New → Blueprint**, sambungkan repository source tadi.
2. Gunakan `render.yaml` di root repository.
3. Periksa: Web Service, Docker, **Free**, wilayah Singapore, satu instance.
4. Ketika diminta, isi secret `DATABASE_URL` dari Neon.
5. Buat/deploy layanan. Tunggu status **Live**.

Bila memakai **New → Web Service** secara manual:

| Pengaturan | Nilai |
| --- | --- |
| Runtime | Docker |
| Root Directory | Kosong jika Dockerfile di root repository |
| Dockerfile Path | `./Dockerfile` |
| Docker Build Context | `.` |
| Instance Type | **Free** |
| Health Check Path | `/health` |
| Auto Deploy | Off untuk tahap uji awal |
| Environment `DATABASE_URL` | URI rahasia Neon |
| Environment `PawPals__RequirePostgres` | `true` |
| Environment `ASPNETCORE_ENVIRONMENT` | `Production` |

Tidak perlu mengisi Build Command/Start Command untuk konfigurasi Docker bawaan ini.
Server mengikuti `PORT` Render dan memakai alamat publik `RENDER_EXTERNAL_URL` yang disediakan Render untuk pemeriksaan origin browser. Native client tetap memakai HTTPS biasa.
Jangan menjalankan dua layanan/replica memakai database yang sama: gerak dan anggota aktif berada di satu proses, bukan di database.

## 7. Coba dengan aplikasi

1. Buka URL layanan ditambah `/health`. Pastikan JSON berisi `server: PawPals.Server`, `status: ok`, dan `protocolVersion: 10`.
2. Pada PawPals yang sudah dibangun ulang, isi **Alamat server** dengan URL dasar Render, tanpa `/health`.
3. Buat room dan bagikan URL dasar + kode kepada teman. Gunakan URL yang sama pada semua client.
4. Uji dua perangkat/jaringan: Join, nama/desain, gerak, interaksi, dua pesan chat berurutan, keluar aplikasi, dan Join lagi.
5. Restart layanan Render. Buat koneksi Join manual memakai kode sebelumnya: kode harus tetap dikenali, anggota offline tidak dipulihkan.
6. Tanpa pemain selama lebih dari 15 menit, uji Join pertama setelah layanan tidur. Tunggu pesan kesiapan, jangan menekan Create berkali-kali.

Mematikan laptop Alfi lalu Join dari perangkat lain adalah bukti penting bahwa hosting sudah mandiri. Uji ini belum dilakukan oleh penyusun.
Saat restart/redeploy layanan, sesi aktif terputus. Pengguna kembali ke mode sendiri lalu Join manual; patch tidak menjanjikan perpindahan sesi tanpa putus.
URL baru membentuk scope history chat lokal baru. History pada URL Cloudflare lama tidak digabung/diubah otomatis.

## 8. Kode room lama dan backup

Tanpa impor, database baru mulai tanpa kode room laptop lama. Buat room baru untuk uji awal bila kode lama tidak diperlukan.
Untuk mempertahankan kode lama, lakukan impor **sebelum deployment pertama**, atau hentikan layanan cloud dulu:

1. Backup file data lokal. Lokasi default server biasa: `%LOCALAPPDATA%\PawPals\Server\rooms.json`; jika pernah memakai `PawPals__DataFile`, pakai lokasi tersebut.
2. Buka terminal sementara di folder solution, lalu isi environment variable `DATABASE_URL` untuk terminal tersebut dengan URI Neon. Jangan menaruhnya dalam file `.cmd`, source, atau riwayat Git.
3. Jalankan perintah berikut, mengganti path JSON dengan path backup milikmu:

```powershell
dotnet run --project .\PawPals.Server -- --import-room-codes "C:\LokasiBackup\rooms.json"
```

4. Impor menggabungkan kode dan aman diulang; tidak mengimpor anggota/token dan tidak mengubah JSON sumber.
5. Tutup terminal yang memegang secret lalu jalankan/deploy kembali layanan. Client tetap Join manual.

Untuk backup katalog cloud, jalankan `SELECT codes FROM pawpals_room_catalog WHERE id = 1;` di SQL Editor Neon dan simpan hasilnya secara privat. Ini daftar kode, bukan format `rooms.json` untuk perintah impor. Jangan publikasikan kode private room. Backup ini bukan backup history chat pemain.
Menghapus project/database Neon tetap menghapus data cloud; penyimpanan PostgreSQL tidak menggantikan backup mandiri.

## 9. Bila bermasalah

| Gejala | Langkah |
| --- | --- |
| Menunggu server bangun | Tunggu sampai dua menit, lalu cek status layanan/kuota di dashboard |
| `/health` tidak ditemukan | Periksa URL layanan dan apakah source sudah dideploy |
| `DATABASE_URL belum diisi` | Isi secret di Render; server sengaja menolak memakai file sementara |
| Database gagal dibaca/disimpan | Periksa status Neon, role, connection string, TLS dan kuota; data tidak otomatis dikosongkan |
| `ROOM_NOT_FOUND` pada kode lama | Database baru belum diimpor atau kode berasal dari server lain |
| Gagal copy `.exe`, MSB3026 | Hentikan proses server/PawPals yang masih berjalan sebelum build |
| Tampilan/tingkah masih versi lama | Jalankan hasil build source terbaru, bukan exe portable lama |

## Rujukan penyedia, dicek 21 November 2026

- [Render Free: sleep, file sementara, kuota, dan database 30 hari](https://render.com/docs/free)
- [Render Web Services](https://render.com/docs/web-services)
- [Render WebSockets](https://render.com/docs/websocket)
- [Render Blueprint](https://render.com/docs/blueprint-spec)
- [Neon Free dan kuota](https://neon.com/pricing)
- [Neon connection string](https://neon.com/docs/connect/connect-from-any-app)
- [TLS Npgsql](https://www.npgsql.org/doc/security.html)
