#include <windows.h>
#include <UIAutomation.h>
#include <winrt/base.h>

// Test fixture only. Uses the modern UIA client to locate actual taskbar launchers.
extern "C" __declspec(dllexport) HRESULT __stdcall LauncherRectangle(int start,RECT* rect,HWND* window) {
    if(!rect || !window)return E_INVALIDARG;
    struct Apartment { HRESULT hr=CoInitializeEx(nullptr,COINIT_MULTITHREADED); ~Apartment(){if(SUCCEEDED(hr))CoUninitialize();} } apartment;
    if(FAILED(apartment.hr))return apartment.hr;
    try {
        winrt::com_ptr<IUIAutomation> uia;
        winrt::check_hresult(CoCreateInstance(CLSID_CUIAutomation8,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(uia.put())));
        auto bounded=uia.as<IUIAutomation2>(); bounded->put_ConnectionTimeout(2000); bounded->put_TransactionTimeout(2000);
        *window=FindWindowW(L"Shell_TrayWnd",nullptr); if(!*window)throw winrt::hresult_error(E_FAIL);
        winrt::com_ptr<IUIAutomationElement> root,node;winrt::check_hresult(uia->ElementFromHandle(*window,root.put()));
        winrt::com_ptr<IUIAutomationCondition> condition;
        winrt::check_hresult(uia->get_RawViewCondition(condition.put()));
        winrt::com_ptr<IUIAutomationElementArray> nodes;
        winrt::check_hresult(root->FindAll(TreeScope_Descendants,condition.get(),nodes.put()));
        int count=0;winrt::check_hresult(nodes->get_Length(&count));
        for(int i=0;i<count;i++) {
            winrt::com_ptr<IUIAutomationElement> item;winrt::check_hresult(nodes->GetElement(i,item.put()));
            BSTR id=nullptr;HRESULT hr=item->get_CurrentAutomationId(&id);
            bool match=SUCCEEDED(hr) && id && wcscmp(id,start?L"StartButton":L"SearchButton")==0;
            SysFreeString(id);if(match){node=item;break;}
        }
        if(!node)throw winrt::hresult_error(E_FAIL);
        BOOL off=TRUE,enabled=FALSE;node->get_CurrentIsOffscreen(&off);node->get_CurrentIsEnabled(&enabled);
        if(off || !enabled)throw winrt::hresult_error(E_ACCESSDENIED);
        winrt::check_hresult(node->get_CurrentBoundingRectangle(rect));
        return S_OK;
    }catch(const winrt::hresult_error& e){return e.code();}
    catch(...){return E_FAIL;}
}
