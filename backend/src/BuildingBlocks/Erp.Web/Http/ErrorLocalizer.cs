using System.Globalization;

namespace Erp.Web.Http;

/// <summary>
/// Human-readable titles for stable error codes in English and Arabic. Clients should translate by <c>code</c>;
/// the title is a convenience for logs, API consumers and server-rendered documents.
/// </summary>
public static class ErrorLocalizer
{
    private static readonly Dictionary<string, (string En, string Ar)> Titles = new(StringComparer.Ordinal)
    {
        ["validation-failed"] = ("One or more fields are invalid.", "حقل واحد أو أكثر غير صالح."),
        ["not-found"] = ("The resource was not found.", "المورد غير موجود."),
        ["forbidden"] = ("You do not have permission to perform this action.", "ليست لديك صلاحية لتنفيذ هذا الإجراء."),
        ["unauthorized"] = ("Authentication is required.", "يلزم تسجيل الدخول."),
        ["auth.invalid-credentials"] = ("The email or password is incorrect.", "البريد الإلكتروني أو كلمة المرور غير صحيحة."),
        ["auth.locked-out"] = ("The account is temporarily locked.", "الحساب مقفل مؤقتًا."),
        ["auth.session-revoked"] = ("Your session has ended. Please sign in again.", "انتهت جلستك. يرجى تسجيل الدخول مرة أخرى."),
        ["auth.refresh-token-invalid"] = ("The session could not be renewed.", "تعذر تجديد الجلسة."),
        ["auth.refresh-token-reused"] = ("This sign-in was revoked for security reasons.", "تم إلغاء تسجيل الدخول لأسباب أمنية."),
        ["company.access-denied"] = ("You do not have access to this company.", "ليست لديك صلاحية على هذه الشركة."),
        ["company.required"] = ("Select a company (X-Company-Id header).", "اختر شركة (ترويسة X-Company-Id)."),
        ["module-disabled"] = ("This module is not enabled for the company.", "هذه الوحدة غير مفعّلة للشركة."),
        ["concurrency-conflict"] = ("The record was changed by someone else. Reload and try again.", "تم تعديل السجل من مستخدم آخر. أعد التحميل وحاول مرة أخرى."),
        ["precondition-required"] = ("Send the If-Match header with the record version.", "أرسل ترويسة If-Match مع إصدار السجل."),
    };

    public static string Title(string code, string fallback)
    {
        if (!Titles.TryGetValue(code, out var title))
        {
            return fallback;
        }

        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar" ? title.Ar : title.En;
    }
}
