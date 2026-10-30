package io.github.geeforceone.trovami.data

import android.content.Context
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.longPreferencesKey
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map

private val Context.dataStore by preferencesDataStore("trovami")

// Who is signed in, on which server. The token is the device token from /pair or /signin.
data class Session(val server: String, val token: String, val name: String, val email: String?)

data class PrefState(
    val session: Session?,
    val householdId: String?,
    val cursor: Long,
    val lastSync: Long,
    val problem: String?,
    val seenVersion: String?,
    val updateCheckedAt: Long,
)

class Prefs(private val context: Context) {
    private object Keys {
        val server = stringPreferencesKey("server")
        val token = stringPreferencesKey("token")
        val name = stringPreferencesKey("name")
        val email = stringPreferencesKey("email")
        val household = stringPreferencesKey("household")
        val cursor = longPreferencesKey("cursor")
        val lastSync = longPreferencesKey("lastSync")
        val problem = stringPreferencesKey("problem")
        val seenVersion = stringPreferencesKey("seenVersion")
        val updateCheckedAt = longPreferencesKey("updateCheckedAt")
    }

    val state: Flow<PrefState> = context.dataStore.data.map { p ->
        PrefState(
            session = sessionOf(p),
            householdId = p[Keys.household],
            cursor = p[Keys.cursor] ?: 0,
            lastSync = p[Keys.lastSync] ?: 0,
            problem = p[Keys.problem],
            seenVersion = p[Keys.seenVersion],
            updateCheckedAt = p[Keys.updateCheckedAt] ?: 0,
        )
    }

    suspend fun current(): PrefState = state.first()

    suspend fun session(): Session? = current().session

    // A new sign-in starts the sync from scratch.
    suspend fun signIn(session: Session) = context.dataStore.edit {
        it[Keys.server] = session.server
        it[Keys.token] = Secrets.encrypt(session.token)
        it[Keys.name] = session.name
        if (session.email != null) it[Keys.email] = session.email else it.remove(Keys.email)
        it[Keys.cursor] = 0
        it.remove(Keys.problem)
    }

    // Forgets the token only; the server is kept to fill in the sign-in form.
    suspend fun signOut() = context.dataStore.edit {
        it.remove(Keys.token)
        it.remove(Keys.household)
        it[Keys.cursor] = 0
        it.remove(Keys.problem)
    }

    suspend fun lastServer(): String? = context.dataStore.data.first()[Keys.server]

    suspend fun setHousehold(id: String?) = context.dataStore.edit {
        if (id != null) it[Keys.household] = id else it.remove(Keys.household)
    }

    suspend fun setSynced(cursor: Long, at: Long) = context.dataStore.edit {
        it[Keys.cursor] = cursor
        it[Keys.lastSync] = at
    }

    suspend fun setProblem(problem: String?) = context.dataStore.edit {
        if (problem != null) it[Keys.problem] = problem else it.remove(Keys.problem)
    }

    suspend fun setSeenVersion(version: String) = context.dataStore.edit { it[Keys.seenVersion] = version }

    suspend fun setUpdateCheckedAt(at: Long) = context.dataStore.edit { it[Keys.updateCheckedAt] = at }

    private fun sessionOf(p: Preferences): Session? {
        val server = p[Keys.server] ?: return null
        val token = p[Keys.token]?.let(Secrets::decrypt) ?: return null
        return Session(server, token, p[Keys.name] ?: "", p[Keys.email])
    }
}
