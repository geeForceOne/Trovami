package io.github.geeforceone.trovami.nfc

import android.nfc.NdefMessage
import android.nfc.NdefRecord
import android.nfc.Tag
import android.nfc.tech.Ndef
import android.nfc.tech.NdefFormatable
import java.io.IOException
import java.security.SecureRandom

// What a tag held when it was scanned.
sealed interface TagRead {
    data class Trovami(val code: String) : TagRead

    // Empty, ready for a Trovami code.
    data object Blank : TagRead

    // Something else is on it (a link, a transit card); it's left alone.
    data object Other : TagRead
}

// NFC tags and QR labels both carry trovami://t/<code> (TagService.cs). Written tags also get an
// Android Application Record, so a scan opens Trovami even where another app claims the URI.
object TrovamiTags {
    const val PREFIX = "trovami://t/"
    private const val ALPHABET = "23456789ABCDEFGHJKMNPQRSTUVWXYZ"
    private const val CODE_LENGTH = 12
    private val random = SecureRandom()

    fun newCode(): String = String(CharArray(CODE_LENGTH) { ALPHABET[random.nextInt(ALPHABET.length)] })

    // The code in a scanned URI, or null when it isn't one of Trovami's.
    fun codeOf(uri: String?): String? {
        if (uri == null || !uri.startsWith(PREFIX, ignoreCase = true)) return null
        val code = uri.substring(PREFIX.length).trim().trimEnd('/').uppercase()
        return code.takeIf { it.length in 6..32 && it.all { c -> c in 'A'..'Z' || c in '0'..'9' || c == '-' || c == '_' } }
    }

    fun read(tag: Tag): TagRead {
        val ndef = Ndef.get(tag)
            ?: return if (NdefFormatable.get(tag) != null) TagRead.Blank else TagRead.Other
        val records = ndef.cachedNdefMessage?.records.orEmpty()
        if (records.all { it.tnf == NdefRecord.TNF_EMPTY }) return TagRead.Blank
        return records.firstNotNullOfOrNull { codeOf(it.toUri()?.toString()) }?.let(TagRead::Trovami) ?: TagRead.Other
    }

    // Must run while the tag is still held to the phone. Throws with a readable message.
    fun write(tag: Tag, code: String, packageName: String) {
        val message = NdefMessage(NdefRecord.createUri(PREFIX + code), NdefRecord.createApplicationRecord(packageName))
        val ndef = Ndef.get(tag)
        if (ndef != null) {
            ndef.use {
                it.connect()
                if (!it.isWritable) throw IOException("This tag is locked and can't be written.")
                if (it.maxSize < message.byteArrayLength) throw IOException("This tag is too small for a Trovami code.")
                it.writeNdefMessage(message)
            }
            return
        }
        val formatable = NdefFormatable.get(tag) ?: throw IOException("This kind of tag can't hold a Trovami code.")
        formatable.use {
            it.connect()
            it.format(message)
        }
    }
}
