#include "pch.h"
#include "ResourceManager.h"
#if __has_include("ResourceManager.g.cpp")
#include "ResourceManager.g.cpp"
#endif

#include <unordered_set>
#include <filesystem>
#include <zip.h>

using namespace std;
using namespace winrt;
using namespace Windows::Foundation;
using namespace Windows::Storage;
using namespace Windows::Web::Http;

namespace winrt::CelestiaAppComponent::implementation
{
    IAsyncAction CopyFolder(StorageFolder source, StorageFolder destination)
    {
        auto cancellation = co_await get_cancellation_token();
        for (const auto& file : co_await source.GetFilesAsync())
        {
            if (cancellation())
                throw_hresult(E_ABORT);
            co_await file.CopyAsync(destination, file.Name(), NameCollisionOption::ReplaceExisting);
        }
        for (const auto& folder : co_await source.GetFoldersAsync())
        {
            if (cancellation())
                throw_hresult(E_ABORT);
            auto newFolder = co_await destination.CreateFolderAsync(folder.Name(), CreationCollisionOption::OpenIfExists);
            co_await CopyFolder(folder, newFolder);
        }
    }

    ResourceManager::ResourceManager(StorageFolder const& addonFolder, StorageFolder const& scriptFolder) : addonFolder(addonFolder), scriptFolder(scriptFolder)
    {
    }

    IAsyncAction WriteDescriptionFile(StorageFolder folder, CelestiaAppComponent::ResourceItem item)
    {
        auto file = co_await folder.CreateFileAsync(L"description.json", CreationCollisionOption::ReplaceExisting);
        co_await FileIO::WriteTextAsync(file, item.JSONRepresentation().Stringify());
    }

    IAsyncAction Unzip(hstring source, StorageFolder destinationFolder)
    {
        auto cancellation = co_await get_cancellation_token();
        auto archive = zip_open(to_string(source).c_str(), 0, nullptr);
        if (!archive)
            throw_hresult(E_FAIL);

        zip_file_t *currentEntry = nullptr;
        bool hasZipError = false;
        try
        {
            for (zip_int64_t i = 0; i < zip_get_num_entries(archive, 0); i += 1)
            {
                // Check cancellation
                if (cancellation())
                    throw_hresult(E_ABORT);

                struct zip_stat st;
                if (zip_stat_index(archive, i, 0, &st) != 0)
                {
                    hasZipError = true;
                    break;
                }
                std::filesystem::path name{ st.name };
                if (!name.has_filename())
                {
                    // is directory
                    auto currentDirectory = destinationFolder;
                    for (auto it = name.begin(); it != name.end(); ++it)
                    {
                        auto component = to_hstring(it->string());
                        if (!component.empty())
                            currentDirectory = co_await currentDirectory.CreateFolderAsync(to_hstring(it->string()), CreationCollisionOption::OpenIfExists);
                    }
                }
                else
                {
                    currentEntry = zip_fopen_index(archive, i, 0);
                    if (!currentEntry)
                    {
                        hasZipError = true;
                        break;
                    }
                    auto currentDirectory = destinationFolder;
                    auto filename = to_hstring(name.filename().string());

                    // The last one is the filename, we don't create directory for it
                    auto it = name.begin();
                    while (true)
                    {
                        auto component = to_hstring(it->string());
                        ++it;
                        if (it == name.end())
                            break;

                        if (!component.empty())
                            currentDirectory = co_await currentDirectory.CreateFolderAsync(component, CreationCollisionOption::OpenIfExists);
                    }

                    auto file = co_await currentDirectory.CreateFileAsync(filename);
                    auto fileStream = co_await file.OpenAsync(FileAccessMode::ReadWrite);
                    auto fileOutputStream = fileStream.GetOutputStreamAt(0);

                    zip_uint64_t bytesWritten = 0;
                    uint32_t bufferSize = 4096;
                    while (bytesWritten != st.size)
                    {
                        // Check cancellation
                        if (cancellation())
                            throw_hresult(E_ABORT);

                        Streams::Buffer buffer{ bufferSize };
                        auto bytesRead = zip_fread(currentEntry, buffer.data(), bufferSize);
                        if (bytesRead < 0)
                        {
                            hasZipError = true;
                            break;
                        }
                        buffer.Length(static_cast<uint32_t>(bytesRead));
                        co_await fileOutputStream.WriteAsync(buffer);
                        bytesWritten += bytesRead;
                    }
                    zip_fclose(currentEntry);
                    currentEntry = nullptr;
                    if (hasZipError)
                    {
                        break;
                    }
                }
            }
        }
        catch (hresult_error const& e)
        {
            // Clean up
            if (currentEntry != nullptr)
                zip_fclose(currentEntry);
            zip_close(archive);

            // Re-throw the error
            throw e;
        }

        // Clean up
        if (currentEntry != nullptr)
            zip_fclose(currentEntry);
        zip_close(archive);

        if (hasZipError)
            throw_hresult(E_FAIL);
    }

    void ResourceManager::Download(CelestiaAppComponent::ResourceItem const& item)
    {
        if (tasks.find(item.ID()) != tasks.end())
            return;
        tasks[item.ID()] = DownloadAsync(item);
    }

    IAsyncAction ResourceManager::DownloadAsync(CelestiaAppComponent::ResourceItem const item)
    {
        auto cancellation = co_await get_cancellation_token();
        HttpClient client;
        bool hasErrors = false;
        try
        {
            auto tempFolder{ ApplicationData::Current().TemporaryFolder() };
            auto tempFile = co_await tempFolder.CreateFileAsync(to_hstring(GuidHelper::CreateNewGuid()) + L".zip");

            auto response = co_await client.GetAsync(Uri(item.URL()), HttpCompletionOption::ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            int64_t total = response.Content().Headers().ContentLength() != nullptr ? response.Content().Headers().ContentLength().Value() : -1L;
            bool canReportProgress = total > 0;
            auto httpStream = co_await response.Content().ReadAsInputStreamAsync();
            auto fileStream = co_await tempFile.OpenAsync(FileAccessMode::ReadWrite);
            auto fileOutputStream = fileStream.GetOutputStreamAt(0);

            int64_t totalRead = 0L;
            bool isMoreToRead = true;

            const size_t bufferSize = 4096;
            using namespace Streams;

            do
            {
                if (cancellation())
                    throw_hresult(E_ABORT);
                auto buffer{ co_await httpStream.ReadAsync(Buffer{ bufferSize }, bufferSize, InputStreamOptions::None) };
                if (buffer.Length() > 0)
                {
                    co_await fileOutputStream.WriteAsync(buffer);
                    totalRead += buffer.Length();
                    if (canReportProgress)
                    {
                        downloadProgressUpdateEvent(*this, make<ResourceManagerDownloadProgressArgs>(item, (double)totalRead / total));
                    }
                }
                else
                {
                    isMoreToRead = false;
                }
            } while (isMoreToRead);

            // try to uninstall first
            co_await Uninstall(item);

            // Create directory
            auto destinationFolder = co_await (item.Type() == L"script" ? scriptFolder : addonFolder).CreateFolderAsync(item.ID(), CreationCollisionOption::OpenIfExists);
            // Extract the archive
            co_await Unzip(tempFile.Path(), destinationFolder);
            // Create description file
            co_await WriteDescriptionFile(destinationFolder, item);
        }
        catch (hresult_error const&)
        {
            hasErrors = true;
        };

        tasks.erase(item.ID());
        if (hasErrors)
        {
            downloadFailureEvent(*this, make<ResourceManagerDownloadFailureArgs>(item));
        }
        else
        {
            downloadSuccessEvent(*this, make<ResourceManagerDownloadSuccessArgs>(item));
        }
    }

    hstring ResourceManager::ItemPath(CelestiaAppComponent::ResourceItem const& item)
    {
        if (item.Type() == L"script")
            return PathHelper::Combine(scriptFolder.Path(), item.ID());
        return PathHelper::Combine(addonFolder.Path(), item.ID());
    }

    hstring ResourceManager::ScriptPath(CelestiaAppComponent::ResourceItem const& item)
    {
        if (item.Type() != L"script")
            return L"";
        auto scriptFileName = item.MainScriptName();
        if (scriptFileName.empty())
            return L"";
        return PathHelper::Combine(ItemPath(item), scriptFileName);
    }

    bool DirectoryExists(hstring const& path)
    {
        auto attribs = GetFileAttributesW(path.c_str());
        return attribs != INVALID_FILE_ATTRIBUTES && ((attribs & FILE_ATTRIBUTE_DIRECTORY) != 0);
    }

    CelestiaAppComponent::ResourceItemState ResourceManager::StateForItem(CelestiaAppComponent::ResourceItem const& item)
    {
        if (tasks.find(item.ID()) != tasks.end())
            return ResourceItemState::Downloading;
        if (DirectoryExists(ItemPath(item)))
            return ResourceItemState::Installed;
        return CelestiaAppComponent::ResourceItemState::None;
    }

    void ResourceManager::Cancel(CelestiaAppComponent::ResourceItem const& item)
    {
        auto task = tasks.find(item.ID());
        if (task != tasks.end())
            task->second.Cancel();
    }

    IAsyncAction ResourceManager::Uninstall(CelestiaAppComponent::ResourceItem const item)
    {
        try
        {
            auto folder = co_await StorageFolder::GetFolderFromPathAsync(ItemPath(item));
            co_await folder.DeleteAsync();
        }
        catch (hresult_error const&) {}
    }

    IAsyncOperation<Collections::IVector<CelestiaAppComponent::ResourceItem>> ResourceManager::InstalledItems()
    {
        auto items = single_threaded_vector<CelestiaAppComponent::ResourceItem>();
        std::unordered_set<hstring> trackedIds;
        // Parse script folder first, because add-on folder might need migration
        try
        {
            for (const auto& folder : co_await scriptFolder.GetFoldersAsync())
            {
                try
                {
                    auto descriptionFile = co_await folder.GetFileAsync(L"description.json");
                    auto fileContent = co_await FileIO::ReadTextAsync(descriptionFile);
                    auto parsedItem = CelestiaAppComponent::ResourceItem::TryParse(fileContent);
                    if (parsedItem != nullptr && parsedItem.ID() == folder.Name() && parsedItem.Type() == L"script")
                    {
                        items.Append(parsedItem);
                        trackedIds.insert(parsedItem.ID());
                    }
                }
                catch (hresult_error const&) {}
            }
        }
        catch (hresult_error const&) {}
        try
        {
            for (const auto& folder : co_await addonFolder.GetFoldersAsync())
            {
                try
                {
                    auto descriptionFile = co_await folder.GetFileAsync(L"description.json");
                    auto fileContent = co_await FileIO::ReadTextAsync(descriptionFile);
                    auto parsedItem = CelestiaAppComponent::ResourceItem::TryParse(fileContent);
                    if (parsedItem != nullptr && parsedItem.ID() == folder.Name() && trackedIds.find(parsedItem.ID()) == trackedIds.end())
                    {
                        if (parsedItem.Type() == L"scripts")
                        {
                            // Perform migration by moving folder to scripts folder
                            try
                            {
                                auto destinationFolder = co_await scriptFolder.CreateFolderAsync(parsedItem.ID(), CreationCollisionOption::OpenIfExists);
                                co_await CopyFolder(folder, destinationFolder);
                                co_await folder.DeleteAsync();
                                items.Append(parsedItem);
                            }
                            catch (hresult_error const&) {}
                        }
                        else 
                        {
                            items.Append(parsedItem);
                        }
                    }
                }
                catch (hresult_error const&) {}
            }
        }
        catch (hresult_error const&) {}
        co_return items;
    }

    event_token ResourceManager::DownloadProgressUpdate(EventHandler<CelestiaAppComponent::ResourceManagerDownloadProgressArgs> const& handler)
    {
        return downloadProgressUpdateEvent.add(handler);
    }

    void ResourceManager::DownloadProgressUpdate(event_token const& token)
    {
        downloadProgressUpdateEvent.remove(token);
    }

    event_token ResourceManager::DownloadSuccess(EventHandler<CelestiaAppComponent::ResourceManagerDownloadSuccessArgs> const& handler)
    {
        return downloadSuccessEvent.add(handler);
    }

    void ResourceManager::DownloadSuccess(event_token const& token)
    {
        downloadSuccessEvent.remove(token);
    }

    event_token ResourceManager::DownloadFailure(EventHandler<CelestiaAppComponent::ResourceManagerDownloadFailureArgs> const& handler)
    {
        return downloadFailureEvent.add(handler);
    }

    void ResourceManager::DownloadFailure(event_token const& token)
    {
        downloadFailureEvent.remove(token);
    }
}
