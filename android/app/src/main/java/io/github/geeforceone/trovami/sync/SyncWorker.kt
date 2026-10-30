package io.github.geeforceone.trovami.sync

import android.content.Context
import androidx.work.BackoffPolicy
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import io.github.geeforceone.trovami.TrovamiApp
import io.github.geeforceone.trovami.data.Session
import io.github.geeforceone.trovami.net.ApiException
import io.github.geeforceone.trovami.net.PushItem
import io.github.geeforceone.trovami.net.PushLocation
import io.github.geeforceone.trovami.net.PushTag
import io.github.geeforceone.trovami.net.SyncPush
import io.github.geeforceone.trovami.photo.Photos
import java.io.IOException
import java.util.concurrent.TimeUnit

// Sends what the phone made offline, then fetches what changed. Runs whenever there's a network;
// when the server can't be reached it tries again later. Everything it sends is idempotent (ids
// are made on the phone), so a retry after a lost answer does no harm.
class SyncWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {
    private val app = context.applicationContext as TrovamiApp
    private val dao = app.db.dao()

    override suspend fun doWork(): Result {
        val session = app.prefs.session() ?: return Result.success()
        return try {
            push(session)
            uploadPhotos(session)
            pull(session)
            Result.success()
        } catch (e: ApiException) {
            when {
                // The device was revoked under Profile: sign in again; unsent items stay.
                e.status == 401 -> app.prefs.signOut().let { Result.success() }
                e.status >= 500 -> Result.retry()
                else -> app.prefs.setProblem(e.message).let { Result.success() }
            }
        } catch (_: IOException) {
            Result.retry()
        }
    }

    private suspend fun push(session: Session) {
        val locations = dao.pendingLocations()
        val tags = dao.pendingTags()
        val items = dao.unpushedItems()
        if (locations.isEmpty() && tags.isEmpty() && items.isEmpty()) return

        val result = app.api.push(
            session,
            SyncPush(
                locations = locations.map { PushLocation(it.id, it.householdId, it.parentId, it.name, it.kind) },
                tags = tags.map { PushTag(it.code, it.locationId) },
                items = items.map { PushItem(it.id, it.householdId, it.locationId, it.name, it.notes, it.quantity, it.hasPhoto) },
            )
        )
        val problems = mutableListOf<String>()
        for (r in result.locations) {
            if (r.ok) dao.locationSent(r.id)
            else dao.deleteLocation(r.id).also { problems += "A new location wasn't saved: ${r.error}" }
        }
        for (r in result.tags) {
            if (r.ok) dao.tagSent(r.id)
            else dao.deleteTag(r.id).also { problems += "A tag wasn't linked: ${r.error}" }
        }
        for (r in result.items) {
            val item = items.firstOrNull { it.id == r.id } ?: continue
            when {
                !r.ok -> {
                    dropItem(item.id)
                    problems += "An item wasn't saved: ${r.error}"
                }
                item.hasPhoto -> dao.itemSent(item.id)
                else -> dao.deleteItem(item.id)
            }
        }
        app.prefs.setProblem(problems.lastOrNull())
    }

    private suspend fun uploadPhotos(session: Session) {
        for (item in dao.photosToUpload()) {
            val file = Photos.file(app, item.id)
            if (file.exists()) {
                try {
                    app.api.uploadPhoto(session, item.id, file)
                } catch (e: ApiException) {
                    if (e.status >= 500 || e.status == 401) throw e
                    app.prefs.setProblem("A photo wasn't saved: ${e.message}")
                }
            }
            dropItem(item.id)
        }
    }

    private suspend fun pull(session: Session) {
        val state = app.prefs.current()
        val pull = app.api.pull(session, state.cursor, dao.householdIds())
        for (dropped in dao.applyPull(pull)) Photos.file(app, dropped.id).delete()
        app.prefs.setSynced(pull.cursor, System.currentTimeMillis())
        if (pull.households.none { it.id == state.householdId })
            app.prefs.setHousehold(pull.households.minByOrNull { it.name.lowercase() }?.id)
    }

    private suspend fun dropItem(id: String) {
        dao.deleteItem(id)
        Photos.file(app, id).delete()
    }

    companion object {
        private const val NAME = "sync"

        // Asks for a sync as soon as there's a network. A request during a running sync runs after it.
        fun request(context: Context) {
            val work = OneTimeWorkRequestBuilder<SyncWorker>()
                .setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build())
                .setBackoffCriteria(BackoffPolicy.EXPONENTIAL, 30, TimeUnit.SECONDS)
                .build()
            WorkManager.getInstance(context).enqueueUniqueWork(NAME, ExistingWorkPolicy.APPEND_OR_REPLACE, work)
        }
    }
}
