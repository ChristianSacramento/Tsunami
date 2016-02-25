#pragma once

#include <libtorrent/entry.hpp>

namespace Tsunami
{
    public ref class Entry
    {
    internal:
        Entry(const libtorrent::entry& e);
        libtorrent::entry* ptr();

    public:
        ~Entry();

    private:
        libtorrent::entry* entry_;
    };
}