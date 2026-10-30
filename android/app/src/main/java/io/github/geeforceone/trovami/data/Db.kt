package io.github.geeforceone.trovami.data

import android.content.Context
import androidx.room.Dao
import androidx.room.Database
import androidx.room.Entity
import androidx.room.Index
import androidx.room.Insert
import androidx.room.PrimaryKey
import androidx.room.Query
import androidx.room.Room
import androidx.room.RoomDatabase
import androidx.room.Transaction
import androidx.room.Upsert
import io.github.geeforceone.trovami.net.SyncPull
import kotlinx.coroutines.flow.Flow

// The phone's copy of the user's households, their location trees and tags, so scanning, linking
// and adding all work in a cellar without signal. Rows marked pending were made on the phone and
// haven't reached the server yet.

@Entity(tableName = "households")
data class HouseholdRow(@PrimaryKey val id: String, val name: String, val isOwner: Boolean)

@Entity(tableName = "locations", indices = [Index("householdId")])
data class LocationRow(
    @PrimaryKey val id: String,
    val householdId: String,
    val parentId: String?,
    val name: String,
    val kind: String,
    val color: String? = null,
    val label: String? = null,
    val pending: Boolean = false,
)

@Entity(tableName = "tags")
data class TagRow(@PrimaryKey val code: String, val householdId: String, val locationId: String, val pending: Boolean = false)

// An item added on the phone. It's deleted once the server has it and its photo.
@Entity(tableName = "items")
data class ItemRow(
    @PrimaryKey val id: String,
    val householdId: String,
    val locationId: String?,
    val name: String?,
    val notes: String?,
    val quantity: Int,
    val hasPhoto: Boolean,
    val createdAt: Long,
    val pushed: Boolean = false,
)

@Dao
abstract class TrovamiDao {
    @Query("SELECT * FROM households ORDER BY name COLLATE NOCASE")
    abstract fun households(): Flow<List<HouseholdRow>>

    @Query("SELECT id FROM households")
    abstract suspend fun householdIds(): List<String>

    @Query("SELECT * FROM locations WHERE householdId = :householdId")
    abstract fun locations(householdId: String): Flow<List<LocationRow>>

    @Query("SELECT * FROM locations WHERE id = :id")
    abstract suspend fun location(id: String): LocationRow?

    @Query("SELECT * FROM tags WHERE code = :code")
    abstract suspend fun tag(code: String): TagRow?

    @Query("SELECT COUNT(*) FROM items")
    abstract fun waitingCount(): Flow<Int>

    @Insert
    abstract suspend fun insertLocation(row: LocationRow)

    @Upsert
    abstract suspend fun upsertTag(row: TagRow)

    @Insert
    abstract suspend fun insertItem(row: ItemRow)

    // Sent in the order they were made, so a new location's parent goes first.
    @Query("SELECT * FROM locations WHERE pending = 1 ORDER BY rowid")
    abstract suspend fun pendingLocations(): List<LocationRow>

    @Query("SELECT * FROM tags WHERE pending = 1")
    abstract suspend fun pendingTags(): List<TagRow>

    @Query("SELECT * FROM items WHERE pushed = 0 ORDER BY createdAt")
    abstract suspend fun unpushedItems(): List<ItemRow>

    @Query("SELECT * FROM items WHERE pushed = 1 AND hasPhoto = 1")
    abstract suspend fun photosToUpload(): List<ItemRow>

    @Query("UPDATE locations SET pending = 0 WHERE id = :id")
    abstract suspend fun locationSent(id: String)

    @Query("DELETE FROM locations WHERE id = :id")
    abstract suspend fun deleteLocation(id: String)

    @Query("UPDATE tags SET pending = 0 WHERE code = :code")
    abstract suspend fun tagSent(code: String)

    @Query("DELETE FROM tags WHERE code = :code")
    abstract suspend fun deleteTag(code: String)

    @Query("UPDATE items SET pushed = 1 WHERE id = :id")
    abstract suspend fun itemSent(id: String)

    @Query("DELETE FROM items WHERE id = :id")
    abstract suspend fun deleteItem(id: String)

    @Upsert
    protected abstract suspend fun upsertHouseholds(rows: List<HouseholdRow>)

    @Upsert
    protected abstract suspend fun upsertLocations(rows: List<LocationRow>)

    @Upsert
    protected abstract suspend fun upsertTags(rows: List<TagRow>)

    @Query("DELETE FROM locations WHERE id IN (:ids)")
    protected abstract suspend fun deleteLocations(ids: List<String>)

    @Query("DELETE FROM tags WHERE code IN (:codes)")
    protected abstract suspend fun deleteTags(codes: List<String>)

    @Query("DELETE FROM households WHERE id NOT IN (:keep)")
    protected abstract suspend fun deleteOtherHouseholds(keep: List<String>)

    @Query("DELETE FROM locations WHERE householdId NOT IN (:keep)")
    protected abstract suspend fun deleteOtherLocations(keep: List<String>)

    @Query("DELETE FROM tags WHERE householdId NOT IN (:keep)")
    protected abstract suspend fun deleteOtherTags(keep: List<String>)

    // Items of a household the user left can't be sent any more. Returns them, for their photos.
    @Query("SELECT * FROM items WHERE householdId NOT IN (:keep)")
    protected abstract suspend fun otherItems(keep: List<String>): List<ItemRow>

    @Query("DELETE FROM items WHERE householdId NOT IN (:keep)")
    protected abstract suspend fun deleteOtherItems(keep: List<String>)

    // Applies a pull in one go. Households always come in full: a missing one was left or deleted.
    @Transaction
    open suspend fun applyPull(pull: SyncPull): List<ItemRow> {
        val keep = pull.households.map { it.id }
        val dropped = otherItems(keep)
        deleteOtherItems(keep)
        deleteOtherTags(keep)
        deleteOtherLocations(keep)
        deleteOtherHouseholds(keep)
        upsertHouseholds(pull.households.map { HouseholdRow(it.id, it.name, it.isOwner) })

        val (goneLocations, locations) = pull.locations.partition { it.deleted }
        deleteLocations(goneLocations.map { it.id })
        upsertLocations(locations.map { LocationRow(it.id, it.householdId, it.parentId, it.name, it.kind, it.color, it.label) })

        val (goneTags, tags) = pull.tags.partition { it.deleted }
        deleteTags(goneTags.map { it.code })
        upsertTags(tags.map { TagRow(it.code, it.householdId, it.locationId) })
        return dropped
    }
}

@Database(entities = [HouseholdRow::class, LocationRow::class, TagRow::class, ItemRow::class], version = 1)
abstract class AppDb : RoomDatabase() {
    abstract fun dao(): TrovamiDao

    companion object {
        fun open(context: Context): AppDb =
            Room.databaseBuilder(context, AppDb::class.java, "trovami.db").build()
    }
}
