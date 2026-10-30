package io.github.geeforceone.trovami

import io.github.geeforceone.trovami.data.LocationRow
import io.github.geeforceone.trovami.net.Api
import io.github.geeforceone.trovami.nfc.TrovamiTags
import io.github.geeforceone.trovami.ui.flattenTree
import io.github.geeforceone.trovami.ui.pathOf
import io.github.geeforceone.trovami.update.Updates
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class LogicTest {
    @Test
    fun tagCodesFollowTheServersRules() {
        assertEquals("K7F3QZ2MNPQR", TrovamiTags.codeOf("trovami://t/k7f3qz2mnpqr/"))
        assertEquals("ABC-12_X", TrovamiTags.codeOf("TROVAMI://t/abc-12_x"))
        assertNull(TrovamiTags.codeOf("trovami://t/abc"))
        assertNull(TrovamiTags.codeOf("trovami://t/has space"))
        assertNull(TrovamiTags.codeOf("https://example.com/t/K7F3QZ"))
        assertNull(TrovamiTags.codeOf(null))
    }

    @Test
    fun newCodesAreValid() {
        repeat(100) {
            val code = TrovamiTags.newCode()
            assertEquals(12, code.length)
            assertEquals(code, TrovamiTags.codeOf(TrovamiTags.PREFIX + code))
        }
    }

    @Test
    fun versionsCompareAsNumbers() {
        assertTrue(Updates.isNewer("1.10.0", "1.9.3"))
        assertTrue(Updates.isNewer("v2.0.0", "1.99.99"))
        assertFalse(Updates.isNewer("1.2.3", "1.2.3"))
        assertFalse(Updates.isNewer("1.2.3", "dev"))
        assertFalse(Updates.isRelease("dev"))
    }

    @Test
    fun serverAddressGetsAScheme() {
        assertEquals("http://192.168.1.10:8337", Api.normalizeServer(" 192.168.1.10:8337/ "))
        assertEquals("https://trovami.example.com", Api.normalizeServer("https://trovami.example.com/"))
    }

    @Test
    fun treeIsFlattenedInNameOrderWithOrphansOnTop() {
        val house = loc("h", null, "House")
        val cellar = loc("c", "h", "Cellar")
        val attic = loc("a", "h", "attic")
        val box = loc("b", "c", "Blue box")
        val orphan = loc("o", "missing", "Bag")
        val flat = flattenTree(listOf(box, cellar, orphan, house, attic))
        assertEquals(listOf("Bag" to 0, "House" to 0, "attic" to 1, "Cellar" to 1, "Blue box" to 2), flat.map { it.first.name to it.second })
        assertEquals("House › Cellar › Blue box", pathOf(box, listOf(house, cellar, box)))
    }

    private fun loc(id: String, parent: String?, name: String) = LocationRow(id, "hh", parent, name, "Box")
}
