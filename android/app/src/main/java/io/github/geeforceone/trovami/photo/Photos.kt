package io.github.geeforceone.trovami.photo

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Matrix
import java.io.File
import kotlin.math.max
import kotlin.math.min

// Photos wait in the app's own storage until they're uploaded. They're shrunk here like the
// server would (longest side 1600 px, JPEG), so uploads from the cellar stay small.
object Photos {
    private const val MAX_SIDE = 1600
    private const val QUALITY = 85

    fun file(context: Context, itemId: String): File = File(File(context.filesDir, "photos").apply { mkdirs() }, "$itemId.jpg")

    fun save(bitmap: Bitmap, rotationDegrees: Int, target: File) {
        val scale = min(1f, MAX_SIDE.toFloat() / max(bitmap.width, bitmap.height))
        val matrix = Matrix().apply {
            postScale(scale, scale)
            postRotate(rotationDegrees.toFloat())
        }
        val shrunk = Bitmap.createBitmap(bitmap, 0, 0, bitmap.width, bitmap.height, matrix, true)
        val partial = File(target.path + ".part")
        partial.outputStream().use { shrunk.compress(Bitmap.CompressFormat.JPEG, QUALITY, it) }
        partial.renameTo(target)
        if (shrunk !== bitmap) shrunk.recycle()
    }

    fun deleteAll(context: Context) {
        File(context.filesDir, "photos").deleteRecursively()
    }
}
