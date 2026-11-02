# kotlinx.serialization and Room bring their own keep rules.

# ML Kit creates its component registrars by reflection, from names in the manifest. Without their
# constructors R8 leaves the barcode detector unregistered, and the QR scanner crashes on opening
# in release builds only.
-keep class * implements com.google.firebase.components.ComponentRegistrar { <init>(); }
