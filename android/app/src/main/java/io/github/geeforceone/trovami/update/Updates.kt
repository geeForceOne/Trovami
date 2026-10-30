package io.github.geeforceone.trovami.update

import android.content.Context
import android.content.Intent
import android.provider.Settings
import androidx.core.content.FileProvider
import androidx.core.net.toUri
import java.io.File

// The app is installed from the APK on each GitHub release; the server tells it which is newest.
object Updates {
    // "1.2.3" as numbers; null for "dev" and anything else that isn't a release.
    private fun parse(version: String): List<Int>? =
        Regex("""^v?(\d+)\.(\d+)\.(\d+)$""").find(version.trim())?.groupValues?.drop(1)?.map(String::toInt)

    fun isRelease(version: String) = parse(version) != null

    fun isNewer(candidate: String, than: String): Boolean {
        val a = parse(candidate) ?: return false
        val b = parse(than) ?: return false
        for (i in 0..2) if (a[i] != b[i]) return a[i] > b[i]
        return false
    }

    fun apkFile(context: Context) = File(File(context.cacheDir, "updates"), "trovami.apk")

    // Android asks once whether Trovami may install apps; until then this opens that setting.
    fun install(context: Context, apk: File) {
        if (!context.packageManager.canRequestPackageInstalls()) {
            context.startActivity(
                Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, "package:${context.packageName}".toUri())
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            )
            return
        }
        val uri = FileProvider.getUriForFile(context, "${context.packageName}.files", apk)
        context.startActivity(
            Intent(Intent.ACTION_VIEW)
                .setDataAndType(uri, "application/vnd.android.package-archive")
                .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
        )
    }
}
