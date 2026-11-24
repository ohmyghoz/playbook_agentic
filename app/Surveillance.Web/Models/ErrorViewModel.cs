using System.Diagnostics;

namespace Surveillance.Web.Models;

public class ErrorViewModel
{
    public int StatusCode { get; init; } = StatusCodes.Status500InternalServerError;
    public required string Title { get; init; }
    public required string Message { get; init; }
    public string? RequestId { get; init; }

    public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);

    /// <summary>What happened and what the user can do next, without technical details.</summary>
    public static ErrorViewModel ForStatus(int statusCode, HttpContext context)
    {
        var (title, message) = statusCode switch
        {
            StatusCodes.Status404NotFound => ("Halaman tidak ditemukan",
                "Alamat ini tidak ada atau investigasinya sudah tidak tersedia. Kembali ke daftar investigasi untuk mencari kasusnya."),
            StatusCodes.Status403Forbidden => ("Akses tidak diizinkan",
                "Akun Anda tidak punya izin untuk membuka halaman ini. Halaman Setting hanya untuk supervisor."),
            StatusCodes.Status400BadRequest => ("Permintaan tidak bisa diproses",
                "Data yang dikirim tidak lengkap atau formulirnya sudah kedaluwarsa. Muat ulang halaman, lalu coba lagi."),
            _ => ("Terjadi kesalahan",
                "Aplikasi gagal memproses permintaan ini. Data investigasi Anda tetap tersimpan. Coba lagi beberapa saat lagi."),
        };

        return new ErrorViewModel
        {
            StatusCode = statusCode,
            Title = title,
            Message = message,
            RequestId = Activity.Current?.Id ?? context.TraceIdentifier,
        };
    }
}
