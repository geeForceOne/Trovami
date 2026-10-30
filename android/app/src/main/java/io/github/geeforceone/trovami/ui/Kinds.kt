package io.github.geeforceone.trovami.ui

import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Apartment
import androidx.compose.material.icons.outlined.Category
import androidx.compose.material.icons.outlined.Checkroom
import androidx.compose.material.icons.outlined.Garage
import androidx.compose.material.icons.outlined.House
import androidx.compose.material.icons.outlined.Inventory2
import androidx.compose.material.icons.outlined.Kitchen
import androidx.compose.material.icons.outlined.Layers
import androidx.compose.material.icons.outlined.MeetingRoom
import androidx.compose.material.icons.outlined.Roofing
import androidx.compose.material.icons.outlined.ShoppingBag
import androidx.compose.material.icons.outlined.Stairs
import androidx.compose.material.icons.outlined.TableRows
import androidx.compose.material.icons.outlined.ViewAgenda
import androidx.compose.material.icons.outlined.ViewModule
import androidx.compose.ui.graphics.vector.ImageVector
import io.github.geeforceone.trovami.data.LocationRow

// The fixed location kinds (LocationKind on the server); a kind only picks the icon.
enum class Kind(val label: String, val icon: ImageVector) {
    House("House", Icons.Outlined.House),
    Apartment("Apartment", Icons.Outlined.Apartment),
    Floor("Floor", Icons.Outlined.Layers),
    Room("Room", Icons.Outlined.MeetingRoom),
    Cellar("Cellar", Icons.Outlined.Stairs),
    Garage("Garage", Icons.Outlined.Garage),
    Attic("Attic", Icons.Outlined.Roofing),
    Closet("Closet", Icons.Outlined.Checkroom),
    Cabinet("Cabinet", Icons.Outlined.Kitchen),
    Shelf("Shelf", Icons.Outlined.TableRows),
    Rack("Rack", Icons.Outlined.ViewModule),
    Drawer("Drawer", Icons.Outlined.ViewAgenda),
    Box("Box", Icons.Outlined.Inventory2),
    Bag("Bag", Icons.Outlined.ShoppingBag),
    Other("Other", Icons.Outlined.Category);

    companion object {
        fun of(name: String): Kind = entries.firstOrNull { it.name.equals(name, ignoreCase = true) } ?: Other
    }
}

// "House › Cellar › Rack 2 › Blue box", as the web app shows it.
fun pathOf(location: LocationRow, all: List<LocationRow>): String {
    val byId = all.associateBy { it.id }
    val names = mutableListOf<String>()
    var current: LocationRow? = location
    while (current != null && names.size < 32) {
        names += current.name
        current = current.parentId?.let(byId::get)
    }
    return names.asReversed().joinToString(" › ")
}

// The tree in display order, each location with its depth. A location whose parent isn't known
// (yet) is shown at the top.
fun flattenTree(all: List<LocationRow>): List<Pair<LocationRow, Int>> {
    val ids = all.mapTo(HashSet()) { it.id }
    val children = all.groupBy { it.parentId?.takeIf(ids::contains) }
    val out = ArrayList<Pair<LocationRow, Int>>(all.size)
    fun walk(parent: String?, depth: Int) {
        for (row in children[parent].orEmpty().sortedBy { it.name.lowercase() }) {
            out += row to depth
            walk(row.id, depth + 1)
        }
    }
    walk(null, 0)
    return out
}
