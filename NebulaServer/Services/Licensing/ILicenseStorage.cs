using NebulaServer.Models.Licensing;

namespace NebulaServer.Services.Licensing;

/// <summary>
/// واجهة تجريدية للتعامل مع تخزين وقراءة التراخيص المحلية.
/// تسمح بتغيير طريقة التخزين (ملفات، قاعدة بيانات، ذاكرة) دون التأثير على LicenseManager.
/// </summary>
public interface ILicenseStorage
{
    /// <summary>
    /// جلب الرخصة من التخزين المحلي.
    /// </summary>
    /// <returns>كائن الرخصة إذا كان موجوداً، أو null إذا لم تكن هناك رخصة محفوظة.</returns>
    Task<LicenseInfo?> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// حفظ الرخصة في التخزين المحلي.
    /// </summary>
    /// <param name="license">الرخصة المراد حفظها.</param>
    Task SaveAsync(LicenseInfo license, CancellationToken cancellationToken = default);

    /// <summary>
    /// حذف الرخصة الحالية من التخزين المحلي (مفيدة عند التفعيل من جديد أو عند إبطال الرخصة).
    /// </summary>
    Task DeleteAsync(CancellationToken cancellationToken = default);
}