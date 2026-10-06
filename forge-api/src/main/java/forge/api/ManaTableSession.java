package forge.api;

import forge.gui.interfaces.IGuiGame;

interface ManaTableSession {
    IGuiGame gui();
    Object platformDialog(String name, Object[] args);
    void publishInput();
    void fail(Throwable error);
}
