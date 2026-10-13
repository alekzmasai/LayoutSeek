#include <windows.h>
#include <appmodel.h>
#include <roapi.h>
#include <UIAutomation.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.UI.UIAutomation.h>
#include <winrt/Windows.UI.UIAutomation.Core.h>
#include <winrt/Windows.ApplicationModel.h>
#include <vector>
#include <string>
#include <cstring>
#ifdef SEARCH_STRESS_TEST
#include <cstdio>
#define TRACE(...) std::fprintf(stderr,__VA_ARGS__)
#else
#define TRACE(...) ((void)0)
#endif

// Windows UI Automation remote-operation ABI (Microsoft.UI.UIAutomation opcode definitions).
// Keep final checks, text replacement and caret placement in one remote batch.
namespace {
using namespace winrt::Windows::UI::UIAutomation::Core;
struct Apartment {
    HRESULT hr;
    Apartment() : hr(RoInitialize(RO_INIT_MULTITHREADED)) {winrt::check_hresult(hr);}
    ~Apartment() {RoUninitialize();}
};
struct Program {
    struct Jump {size_t offset; int instruction;};
    std::vector<uint8_t> bytes;
    std::vector<Jump> guards;
    int count=0, next=1;
    Program() {number(0);}
    void number(int32_t value) {auto p=reinterpret_cast<uint8_t*>(&value); bytes.insert(bytes.end(),p,p+4);}
    void instruction(std::initializer_list<int32_t> values) {for(auto v:values) number(v); ++count;}
    int integer(int value) {int id=next++; instruction({0x1d,id,value}); return id;}
    int unsignedInteger(unsigned value) {int id=next++; instruction({0x1e,id,static_cast<int>(value)}); return id;}
    int boolean(bool value) {int id=next++; instruction({0x1f,id}); bytes.push_back(value ? 1 : 0); return id;}
    int text(const std::wstring& value) {int id=next++; instruction({0x22,id,static_cast<int>(value.size())}); auto p=reinterpret_cast<const uint8_t*>(value.data()); bytes.insert(bytes.end(),p,p+value.size()*2); return id;}
    int property(int object,int property,bool ignore=true) {int key=integer(property), skipDefault=boolean(ignore), id=next++; instruction({0x38,id,object,key,skipDefault}); return id;}
    int equal(int left,int right) {int id=next++; instruction({0x1c,id,left,right,0}); return id;}
    void require(int condition) {guards.push_back({bytes.size()+8,count}); instruction({0x03,condition,0});}
    void requireFalse(int condition) {guards.push_back({bytes.size()+8,count}); instruction({0x02,condition,0});}
    void requireEqual(int left,int right) {require(equal(left,right));}
    int pattern(int object,int patternId) {int id=next++; instruction({patternId,id,object}); return id;}
    int rangeText(int range,int length) {int id=next++; instruction({(UIA_TextPatternId<<16)|(1<<8)|12,id,range,length}); return id;}
    void finish() {for(auto guard:guards) {int distance=count-guard.instruction; std::memcpy(bytes.data()+guard.offset,&distance,4);} instruction({0x05});}
};
bool IsSearchTarget(IUIAutomationElement* element, HWND expectedWindow, DWORD expectedPid) {
    if(GetForegroundWindow()!=expectedWindow) return false;
    DWORD foregroundPid=0; GetWindowThreadProcessId(expectedWindow,&foregroundPid);
    int elementPid=0; if(FAILED(element->get_CurrentProcessId(&elementPid)) || expectedPid!=foregroundPid || expectedPid!=static_cast<DWORD>(elementPid)) return false;
    BSTR id=nullptr; if(FAILED(element->get_CurrentAutomationId(&id))) return false;
    bool matches=id && std::wstring(id)==L"SearchTextBox"; SysFreeString(id); if(!matches) return false;
    HANDLE process=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,expectedPid);
    wchar_t path[32768], windows[MAX_PATH]; DWORD size=32768;
    bool read=process && QueryFullProcessImageNameW(process,0,path,&size); if(process) CloseHandle(process);
    if(!read || !GetWindowsDirectoryW(windows,MAX_PATH)) return false;
    std::wstring expected=std::wstring(windows)+L"\\SystemApps\\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\\SearchHost.exe";
    return _wcsicmp(path,expected.c_str())==0;
}
HRESULT Replace(HWND window,DWORD pid,const int* runtimeId,int runtimeIdLength,const wchar_t* expected,const wchar_t* replacement,int* outcome,int repetitions=0) {
    if(!expected || !replacement || !outcome || !runtimeId || runtimeIdLength<=0 || runtimeIdLength>64 || wcslen(expected)>80 || wcslen(replacement)>80) return E_INVALIDARG;
    *outcome=0;
    try {
        Apartment apartment;
        auto automation=winrt::create_instance<IUIAutomation>(CLSID_CUIAutomation8);
        winrt::com_ptr<IUIAutomationElement> element;
        winrt::check_hresult(automation->GetFocusedElement(element.put()));
        if(!element || !IsSearchTarget(element.get(),window,pid)) return S_OK;
        SAFEARRAY* currentId=nullptr; winrt::check_hresult(element->GetRuntimeId(&currentId));
        LONG lower=0,upper=-1; SafeArrayGetLBound(currentId,1,&lower); SafeArrayGetUBound(currentId,1,&upper);
        bool same=(upper-lower+1)==runtimeIdLength;
        if(same) {int* ids=nullptr; winrt::check_hresult(SafeArrayAccessData(currentId,reinterpret_cast<void**>(&ids))); for(int i=0;i<runtimeIdLength;i++) if(ids[i]!=runtimeId[i])same=false; SafeArrayUnaccessData(currentId);}
        SafeArrayDestroy(currentId); if(!same)return S_OK;
        CoreAutomationRemoteOperation operation;
        operation.ImportElement({0},element.as<winrt::Windows::UI::UIAutomation::AutomationElement>());
        const int setValue=(UIA_ValuePatternId<<10)|3;
        if(!operation.IsOpcodeSupported(setValue)) return E_NOTIMPL;
        Program p;
        int result=p.integer(0); operation.AddToResults({result});
        int zero=p.integer(0),one=p.integer(1),all=p.integer(-1);
        int before=p.text(expected),after=p.text(replacement);
        int focus=p.property(0,UIA_HasKeyboardFocusPropertyId);
#ifdef SEARCH_STRESS_TEST
        operation.AddToResults({focus});
#endif
        p.require(focus);
        p.require(p.property(0,UIA_IsEnabledPropertyId));
        p.requireFalse(p.property(0,UIA_IsOffscreenPropertyId));
        p.requireEqual(p.property(0,UIA_ValueValuePropertyId),before);
        int text=p.pattern(0,UIA_TextPatternId);
        int selections=p.next++; p.instruction({(UIA_TextPatternId<<10)|5,selections,text});
        int selectionCount=p.next++; p.instruction({0x2e,selectionCount,selections}); p.requireEqual(selectionCount,p.unsignedInteger(1));
        int index=p.unsignedInteger(0);
        int selected=p.next++; p.instruction({0x2d,selected,selections,index});
        int endpoints=p.next++; p.instruction({(UIA_TextPatternId<<16)|(1<<8)|5,endpoints,selected,zero,selected,one}); p.requireEqual(endpoints,zero);
        int document=p.next++; p.instruction({(UIA_TextPatternId<<10)|7,document,text});
        p.instruction({(UIA_TextPatternId<<16)|(1<<8)|15,document,one,selected,zero});
        p.requireEqual(p.rangeText(document,all),before);
        // Stress builds deliberately enlarge the in-provider interval. No input capture or hooks.
        for(int i=0;i<repetitions && i<5000;i++) p.property(0,UIA_ValueValuePropertyId);
        p.require(p.property(0,UIA_HasKeyboardFocusPropertyId));
        p.requireFalse(p.property(0,UIA_IsOffscreenPropertyId));
        p.requireFalse(p.property(0,UIA_ValueIsReadOnlyPropertyId));
        int value=p.pattern(0,UIA_ValuePatternId);
        p.instruction({setValue,value,after}); p.instruction({0x1d,result,1});
        int updated=p.next++; p.instruction({(UIA_TextPatternId<<10)|7,updated,text});
        p.instruction({(UIA_TextPatternId<<16)|(1<<8)|15,updated,one,updated,zero});
        int length=p.integer(static_cast<int>(wcslen(replacement))),moved=p.next++;
        p.instruction({(UIA_TextPatternId<<16)|(1<<8)|14,moved,updated,one,zero,length}); p.requireEqual(moved,length);
        p.requireEqual(p.rangeText(updated,all),after);
        p.instruction({(UIA_TextPatternId<<16)|(1<<8)|15,updated,zero,updated,one});
        p.instruction({(UIA_TextPatternId<<16)|(1<<8)|16,updated});
        p.requireEqual(p.property(0,UIA_ValueValuePropertyId),after);
        p.instruction({0x1d,result,2}); p.finish();
        if(GetForegroundWindow()!=window) return S_OK;
        TRACE("remote execute: instructions=%d bytes=%zu\n",p.count,p.bytes.size());
        auto completed=operation.Execute(p.bytes);
        TRACE("remote status=%d error=%08x location=%d hasResult=%d\n",static_cast<int>(completed.Status()),static_cast<unsigned>(completed.ExtendedError()),completed.ErrorLocation(),completed.HasOperand({result}));
#ifdef SEARCH_STRESS_TEST
        if(completed.HasOperand({focus})) {auto v=completed.GetOperand({focus}); auto prop=v.try_as<winrt::Windows::Foundation::IPropertyValue>(); TRACE("focus value null=%d property=%d type=%d\n",!v,!!prop,prop?static_cast<int>(prop.Type()):-1); if(prop && prop.Type()==winrt::Windows::Foundation::PropertyType::Boolean) TRACE("focus bool=%d\n",prop.GetBoolean());}
#endif
        if(completed.HasOperand({result})) *outcome=winrt::unbox_value<int>(completed.GetOperand({result}));
        if(completed.Status()!=AutomationRemoteOperationStatus::Success) return FAILED(completed.ExtendedError()) ? completed.ExtendedError() : E_FAIL;
        return S_OK;
    } catch(const winrt::hresult_error& error) {TRACE("native error=%08x\n",static_cast<unsigned>(error.code())); return error.code();} catch(...) {return E_FAIL;}
}
HRESULT HasPackage() {UINT32 length=0; LONG error=GetCurrentPackageFullName(&length,nullptr); return error==ERROR_INSUFFICIENT_BUFFER ? S_OK : HRESULT_FROM_WIN32(error);}
}
extern "C" __declspec(dllexport) HRESULT __stdcall SearchReplace(HWND window,DWORD pid,const int* runtimeId,int runtimeIdLength,const wchar_t* expected,const wchar_t* replacement,int* outcome) {
    return Replace(window,pid,runtimeId,runtimeIdLength,expected,replacement,outcome);
}
#ifdef SEARCH_STRESS_TEST
extern "C" __declspec(dllexport) HRESULT __stdcall SearchReplaceStress(HWND window,DWORD pid,const int* runtimeId,int runtimeIdLength,const wchar_t* expected,const wchar_t* replacement,int* outcome,int repetitions) {
    return Replace(window,pid,runtimeId,runtimeIdLength,expected,replacement,outcome,repetitions);
}
#endif
extern "C" __declspec(dllexport) HRESULT __stdcall SearchStartupState(int* state) {
    if(!state)return E_INVALIDARG; *state=0; HRESULT package=HasPackage(); if(FAILED(package))return package;
    try {Apartment apartment; auto task=winrt::Windows::ApplicationModel::StartupTask::GetAsync(L"SearchLayoutStartup").get(); *state=static_cast<int>(task.State()); return S_OK;} catch(const winrt::hresult_error& error){return error.code();} catch(...){return E_FAIL;}
}
extern "C" __declspec(dllexport) HRESULT __stdcall SearchSetStartup(int enable,int* state) {
    if(!state)return E_INVALIDARG; *state=0; HRESULT package=HasPackage(); if(FAILED(package))return package;
    try {Apartment apartment; auto task=winrt::Windows::ApplicationModel::StartupTask::GetAsync(L"SearchLayoutStartup").get(); if(enable)*state=static_cast<int>(task.RequestEnableAsync().get()); else{task.Disable(); *state=static_cast<int>(task.State());} return S_OK;} catch(const winrt::hresult_error& error){return error.code();} catch(...){return E_FAIL;}
}
